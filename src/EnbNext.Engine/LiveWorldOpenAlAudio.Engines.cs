using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
using Silk.NET.OpenAL;

namespace EnbNext.Engine;

internal sealed unsafe partial class LiveWorldOpenAlAudio
{
    private readonly Dictionary<ulong, EngineSoundInstance> _engineSounds = [];
    private readonly Dictionary<string, EffectSoundResource> _engineSoundResources = new(StringComparer.OrdinalIgnoreCase);
    private string _engineSoundStatus = "engine SFX idle";
    private long _engineSoundsStarted;

    public void UpdateEngines(IReadOnlyList<IEnbRenderObject> objects, EnbRetailAssetBundle assets,
        EnbResolvedRenderCamera camera, float gain, uint sampleTime, bool dopplerEnabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_alc is not null && _context != null && !_alc.MakeContextCurrent(_context))
        { _engineSoundStatus = "engine SFX blocked: cannot bind context"; return; }
        var retained = new HashSet<ulong>();
        int pending = 0, blocked = 0;
        string? failure = null;
        // Off-screen ships are still audible. The scanner has no audio owner;
        // render identity, not target selection or visual generation, owns a voice.
        foreach (var obj in objects)
        {
            if (assets.Sounds is null || EnbCurrentRetailEngineSoundRuntime.ResolveName(obj, assets.Sounds) is not string name ||
                obj.Spatial?.Transform.Position is not Vector3 position ||
                !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) continue;
            if (!retained.Add(obj.RenderIdentity)) continue;
            if (_engineSounds.TryGetValue(obj.RenderIdentity, out var previous) && previous.Name != name)
            { RetireEngineSound(previous); _engineSounds.Remove(obj.RenderIdentity); }
            if (!_engineSounds.TryGetValue(obj.RenderIdentity, out var instance))
                _engineSounds.Add(obj.RenderIdentity, instance = new EngineSoundInstance(name));
            if (!EnbCurrentRetailEngineSoundRuntime.ShouldPlay(obj))
                RetireEngineSound(instance);
            try
            {
                if (instance.Error is not null) { blocked++; failure ??= instance.Error; continue; }
                // Prewarm while idle, off the render thread. Idle ships never open a source.
                if (!_engineSoundResources.TryGetValue(name, out var resource))
                {
                    if (_engineSoundResources.Count >= 64)
                    {
                        var victim = _engineSoundResources.FirstOrDefault(r => r.Value.Load.IsCompleted &&
                            !_engineSounds.Values.Any(s => s.Name == r.Key));
                        if (victim.Key is null) { blocked++; failure ??= "engine sound cache limit"; continue; }
                        if (victim.Value.Buffer != 0) _al!.DeleteBuffer(victim.Value.Buffer);
                        _engineSoundResources.Remove(victim.Key);
                    }
                    resource = new EffectSoundResource(Task.Run(() =>
                    {
                        EffectDecodeSlots.Wait();
                        try { return DecodeEffectSound(name, assets, engineLoop: true); }
                        finally { EffectDecodeSlots.Release(); }
                    }));
                    _engineSoundResources.Add(name, resource);
                }
                if (!EnbCurrentRetailEngineSoundRuntime.ShouldPlay(obj)) continue;
                if (!resource.Load.IsCompleted) { pending++; continue; }
                var decoded = resource.Load.GetAwaiter().GetResult();
                if (decoded.Error is not null) throw new InvalidDataException(decoded.Error);
                if (decoded.MissingMedia) continue;
                var definition = decoded.Definition!;
                var pcm = decoded.Pcm!;
                if (pcm.Channels != 1) throw new InvalidDataException("Engine sound requires mono PCM");
                if (!EnsureInitialized()) { blocked++; failure ??= _status; continue; }
                if (!_alc!.MakeContextCurrent(_context)) throw new InvalidOperationException("Cannot bind engine audio context");
                if (instance.Source == 0)
                {
                    if (_engineSounds.Values.Count(s => s.Source != 0) >= 64)
                    { blocked++; failure ??= $"{name}: maximum engine instances reached"; continue; }
                    if (resource.Buffer == 0)
                    {
                        resource.Buffer = _al!.GenBuffer();
                        fixed (byte* data = pcm.Pcm16LittleEndian)
                            _al.BufferData(resource.Buffer, BufferFormat.Mono16, data, pcm.Pcm16LittleEndian.Length, pcm.SampleRate);
                        CheckMusicError("engine buffer upload");
                    }
                    instance.Source = _al!.GenSource();
                    CheckMusicError("engine source creation");
                    RegisterVoice(instance.Source,name,definition.EffectiveMaxInstances,definition.EffectiveSoundType,definition.EffectivePriority,EnbRetailSoundArbitration.SpatialScore(position-camera.Position,definition.EffectiveDropOffRadius),spatialMono:UsesSpatialBank(pcm));
                    CheckMusicError("engine voice admission");
                    instance.Doppler = new EnbCurrentRetailEngineDopplerRuntime(definition);
                    instance.Doppler.Update(position, camera.Position, sampleTime, dopplerEnabled);
                    _al.SetSourceProperty(instance.Source, SourceInteger.Buffer, (int)resource.Buffer);
                    CheckMusicError($"engine buffer binding {resource.Buffer}");
                    _al.SetSourceProperty(instance.Source, SourceBoolean.Looping, true);
                    _al.SetSourceProperty(instance.Source, SourceBoolean.SourceRelative, true);
                    _al.SetSourceProperty(instance.Source, SourceFloat.RolloffFactor, 1f);
                    _al.SetSourceProperty(instance.Source, SourceFloat.ReferenceDistance, Math.Max(1, definition.EffectiveMaxVolumeRadius));
                    _al.SetSourceProperty(instance.Source, SourceFloat.MaxDistance, definition.EffectiveDropOffRadius);
                    CheckMusicError($"engine distances {definition.EffectiveMaxVolumeRadius}/{definition.EffectiveDropOffRadius}");
                    _al.SetSourceProperty(instance.Source, SourceFloat.Pitch, definition.EffectivePitch);
                    CheckMusicError($"engine pitch {definition.EffectivePitch}");
                    _al.DistanceModel(DistanceModel.LinearDistanceClamped);
                    ApplyCameraRelativePosition(_al, instance.Source, camera, position);
                    CheckMusicError($"engine position {position} camera {camera.Position}");
                    SetVoiceGain(instance.Source, definition.EffectiveVolume * Math.Clamp(gain, 0, 1));
                    CheckMusicError("engine configure");
                    _al.SourcePlay(instance.Source);
                    CheckMusicError("engine start");
                    _engineSoundsStarted++;
                }
                else
                {
                    ApplyCameraRelativePosition(_al!, instance.Source, camera, position);
                    _al!.SetSourceProperty(instance.Source, SourceFloat.Pitch,
                        instance.Doppler!.Update(position, camera.Position, sampleTime, dopplerEnabled));
                    SetVoiceGain(instance.Source, definition.EffectiveVolume * Math.Clamp(gain, 0, 1));
                    SetVoiceScore(instance.Source,EnbRetailSoundArbitration.SpatialScore(position-camera.Position,definition.EffectiveDropOffRadius));
                    CheckMusicError("engine update");
                }
            }
            catch (Exception ex)
            { RetireEngineSound(instance); instance.Error = $"{name}: {ex.Message}"; blocked++; failure ??= instance.Error; }
        }
        foreach (var key in _engineSounds.Keys.Where(k => !retained.Contains(k)).ToArray())
        { RetireEngineSound(_engineSounds[key]); _engineSounds.Remove(key); }
        var active = _engineSounds.Values.Where(s => s.Source != 0).ToArray();
        _engineSoundStatus = $"engine SFX playing {active.Length} pending {pending} blocked {blocked} starts {_engineSoundsStarted}" +
            $" · {string.Join(',', active.Select(s => s.Name).Distinct())} · Doppler {(dopplerEnabled ? "on" : "off")}" +
            (active.Length == 0 ? "" : FormattableString.Invariant($" pitch {active.Min(s => s.Doppler!.Pitch):F3}..{active.Max(s => s.Doppler!.Pitch):F3}")) +
            (failure is null ? "" : $" · {failure}");
    }

    private void RetireEngineSound(EngineSoundInstance instance)
    {
        if (instance.Source != 0 && _al is not null)
        { _al.SourceStop(instance.Source); DeleteVoiceSource(instance.Source); _al.GetError(); }
        instance.Source = 0;
        instance.Doppler = null;
    }

    private void StopEngineSounds(bool clearCache = false)
    {
        if (_alc is not null && _context != null) _alc.MakeContextCurrent(_context);
        foreach (var instance in _engineSounds.Values) RetireEngineSound(instance);
        _engineSounds.Clear();
        if (clearCache)
        {
            foreach (var resource in _engineSoundResources.Values)
                if (resource.Buffer != 0) _al!.DeleteBuffer(resource.Buffer);
            _engineSoundResources.Clear();
        }
        _engineSoundStatus = "engine SFX stopped";
    }

    private sealed class EngineSoundInstance(string name)
    { public string Name { get; } = name; public uint Source; public string? Error; public EnbCurrentRetailEngineDopplerRuntime? Doppler; }
}
