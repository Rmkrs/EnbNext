using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using Silk.NET.OpenAL;

namespace EnbNext.Engine;

/// <summary>
/// R6Z27 current-retail RSL Sound output/completion boundary. The current retail
/// semantics are resolved before this class is called; this class only owns
/// OpenAL device/context/source/buffer lifetime and camera-relative 3D output.
/// </summary>
internal sealed unsafe partial class LiveWorldOpenAlAudio : IDisposable
{
    private static bool focusMuted;
    internal static bool FocusMuted => Volatile.Read(ref focusMuted);
    private static event Action? FocusOutputChanged;
    internal static void SetFocusMuted(bool value)
    {
        if(focusMuted==value)return;
        Volatile.Write(ref focusMuted,value);FocusOutputChanged?.Invoke();
    }
    public LiveWorldOpenAlAudio() { FocusOutputChanged+=UpdateDiagnosticOutput; if(DeveloperRenderDiagnostics.Available) DeveloperRenderDiagnostics.Changed+=UpdateDiagnosticOutput; }
    private void UpdateDiagnosticOutput()
    {
        if(_disposed || _al is null || _alc is null || _context == null)return;
        var prior=_alc.GetCurrentContext();
        try
        {
            if(_alc.MakeContextCurrent(_context))
                _al.SetListenerProperty(ListenerFloat.Gain, !focusMuted && DeveloperRenderDiagnostics.Audio && Environment.GetEnvironmentVariable("ENB_AUDIO_MUTE_OUTPUT")!="1" ? 1f : 0f);
        }
        finally { _alc.MakeContextCurrent(prior); }
    }
    private readonly Dictionary<LiveRslSoundInstanceKey, OpenAlSoundInstance> _instances = [];
    private readonly List<OpenAlSoundInstance> _rslSoundTails = [];
    private readonly Dictionary<string, OpenAlSoundBuffer> _buffers = new(StringComparer.OrdinalIgnoreCase);

    private ALContext? _alc;
    private AL? _al;
    private Device* _device;
    private Context* _context;
    private bool _initializationAttempted;
    internal bool MemoryOnlyOutput { get; set; }
    private string _status = "OpenAL not requested";
    private bool _disposed;

    public string StatusText => $"{_status} · {_musicStatus} · {_effectSoundStatus} · {_engineSoundStatus}";
    public string DeviceName { get; private set; } = "not opened";

    public IReadOnlyList<LiveRslSoundCompletionEvent> Update(
        EnbResolvedRenderCamera camera,
        IReadOnlyList<LiveRslSoundRequest> requests,
        EnbMixResourceCatalog? retailMixResources,
        uint currentClientTime,
        float soundGain = 1f)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LiveWorldOpenAlAudio));
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count == 0 && _instances.Count==0 && _rslSoundTails.Count==0)
        {
            Speech.Retire(this);
            StopAllSources();
            _status = _initializationAttempted ? "OpenAL idle · no active bounded RSL Sound" : "OpenAL idle · not initialized";
            return Array.Empty<LiveRslSoundCompletionEvent>();
        }

        if (!EnsureInitialized())
            return Array.Empty<LiveRslSoundCompletionEvent>();

        var completions = new List<LiveRslSoundCompletionEvent>();
        try
        {
            if (_alc is null || _al is null || _context == null || !_alc.MakeContextCurrent(_context))
            {
                _status = "OpenAL unavailable · failed to make the audio context current";
                return completions;
            }

            _al.GetError();
            foreach(var tail in _rslSoundTails.ToArray())
            {
                _al.GetSourceProperty(tail.Source,GetSourceInteger.SourceState,out int state);
                if(state!=(int)SourceState.Playing)
                {
                    DeleteVoiceSource(tail.Source);_rslSoundTails.Remove(tail);continue;
                }
                ApplyCameraRelativePosition(_al,tail.Source,camera,tail.LastWorldPosition);
                SetVoiceScore(tail.Source,EnbRetailSoundArbitration.SpatialScore(tail.LastWorldPosition-camera.Position,tail.Plan.MaximumDistance));
                SetVoiceGain(tail.Source,tail.Plan.Gain*Math.Clamp(soundGain,0,1));
            }
            _al.DistanceModel(DistanceModel.LinearDistanceClamped);
            if (_al.GetError() != AudioError.NoError)
            {
                _status = "OpenAL unavailable · failed to select linear clamped distance attenuation";
                return completions;
            }

            var requestedKeys = new HashSet<LiveRslSoundInstanceKey>();
            int started = 0;
            int updated = 0;
            int completed = 0;
            string? firstFailure = null;
            string? firstReceipt = null;

            foreach (LiveRslSoundRequest request in requests)
            {
                requestedKeys.Add(request.Key);
                // 0045FB40 resets an existing handle on repeated activation;
                // it does not leave a second orphan playing alongside it.
                foreach(var old in _instances.Values.Where(v=>request.HandleId!=0 && v.HandleId==request.HandleId && v.Key!=request.Key).ToArray())
                    RetireInstance(old.Key);
                if(_instances.TryGetValue(request.Key,out var replaced) && replaced.HandleId!=request.HandleId)
                    RetireInstance(request.Key,release:true);
                if(_instances.TryGetValue(request.Key,out var reset) && reset.PlaybackOrdinal!=request.PlaybackOrdinal)
                    RetireInstance(request.Key);
                if (_instances.TryGetValue(request.Key, out OpenAlSoundInstance? instance))
                {
                    if (!request.Plan.LoopContinuously)
                    {
                        _al.GetSourceProperty(instance.Source, GetSourceInteger.SourceState, out int sourceState);
                        AudioError stateError = _al.GetError();
                        if (stateError != AudioError.NoError)
                        {
                            firstFailure ??= $"{request.LogicalSoundName}: OpenAL source-state query failed ({stateError})";
                            continue;
                        }

                        if (sourceState != (int)SourceState.Playing)
                        {
                            completions.Add(new LiveRslSoundCompletionEvent(request.Key, currentClientTime) {PlaybackOrdinal=request.PlaybackOrdinal});
                            firstReceipt ??= DescribeInstance(request, instance.Buffer, "completed", camera, worldPosition: instance.FixedWorldPosition);
                            RetireInstance(request.Key);
                            completed++;
                            continue;
                        }
                    }

                    Vector3 worldPosition = !request.UpdateEnabled ? instance.LastWorldPosition : request.Plan.FollowsLiveObjectTransform
                        ? request.WorldPosition
                        : instance.FixedWorldPosition;
                    instance.LastWorldPosition=worldPosition;
                    ApplyCameraRelativePosition(_al, instance.Source, camera, worldPosition);
                    SetVoiceScore(instance.Source,EnbRetailSoundArbitration.SpatialScore(worldPosition-camera.Position,request.Plan.MaximumDistance));
                    SetVoiceGain(instance.Source, request.Plan.Gain * Math.Clamp(soundGain, 0f, 1f));
                    updated++;
                    firstReceipt ??= DescribeInstance(request, instance.Buffer, "playing", camera, worldPosition);
                    continue;
                }

                // 00460E80 gates 0045FC50, including its first playback start.
                if (!request.UpdateEnabled) continue;
                if (!TryGetOrCreateBuffer(request, retailMixResources, out OpenAlSoundBuffer buffer, out string? bufferFailure))
                {
                    firstFailure ??= $"{request.LogicalSoundName}: {bufferFailure ?? "buffer creation failed"}";
                    continue;
                }

                uint source = _al.GenSource();
                AudioError sourceError = _al.GetError();
                if (source == 0 || sourceError != AudioError.NoError)
                {
                    firstFailure ??= $"{request.LogicalSoundName}: OpenAL source allocation failed ({sourceError})";
                    continue;
                }

                try
                {
                    RegisterVoice(source,request.LogicalSoundName,request.Plan.MaximumInstances,request.Plan.SoundType,request.Plan.Priority,EnbRetailSoundArbitration.SpatialScore(request.WorldPosition-camera.Position,request.Plan.MaximumDistance),spatialMono:buffer.Channels==1 && buffer.SourceFormat.StartsWith("PCM",StringComparison.Ordinal));
                    _al.SetSourceProperty(source, SourceInteger.Buffer, buffer.BufferId);
                    _al.SetSourceProperty(source, SourceBoolean.SourceRelative, true);
                    _al.SetSourceProperty(source, SourceBoolean.Looping, request.Plan.LoopContinuously);
                    SetVoiceGain(source, request.Plan.Gain * Math.Clamp(soundGain, 0f, 1f));
                    _al.SetSourceProperty(source, SourceFloat.Pitch, request.Plan.Pitch);
                    _al.SetSourceProperty(source, SourceFloat.ReferenceDistance, request.Plan.ReferenceDistance);
                    _al.SetSourceProperty(source, SourceFloat.MaxDistance, request.Plan.MaximumDistance);
                    _al.SetSourceProperty(source, SourceFloat.RolloffFactor, 1f);
                    ApplyCameraRelativePosition(_al, source, camera, request.WorldPosition);

                    AudioError configureError = _al.GetError();
                    if (configureError != AudioError.NoError)
                        throw new InvalidOperationException($"OpenAL source configuration failed ({configureError}).");

                    _al.SourcePlay(source);
                    AudioError playError = _al.GetError();
                    if (playError != AudioError.NoError)
                        throw new InvalidOperationException($"OpenAL source start failed ({playError}).");

                    instance = new OpenAlSoundInstance(
                        request.Key,
                        request.LogicalSoundName,
                        source,
                        buffer,
                        request.WorldPosition,request.Plan,request.HandleId) {PlaybackOrdinal=request.PlaybackOrdinal};
                    _instances.Add(request.Key, instance);
                    started++;
                    firstReceipt ??= DescribeInstance(request, buffer, "started", camera, request.WorldPosition);
                }
                catch (Exception ex)
                {
                    _al.SourceStop(source);
                    DeleteVoiceSource(source);
                    _al.GetError();
                    firstFailure ??= $"{request.LogicalSoundName}: {ex.Message}";
                }
            }

            foreach (LiveRslSoundInstanceKey key in _instances.Keys.Where(key => !requestedKeys.Contains(key)).ToArray())
                RetireInstance(key,release:true);

            string counts = $"OpenAL active {_instances.Count} · started {started} · updated {updated} · completed {completed}";
            if (firstReceipt is not null)
                counts += $" · {firstReceipt}";
            if (firstFailure is not null)
                counts += $" · blocked {firstFailure}";
            _status = counts;
        }
        catch (Exception ex)
        {
            _status = $"OpenAL runtime failure · {ex.GetType().Name}: {ex.Message}";
        }

        return completions;
    }

    public void StopAll()
    {
        if (_disposed)
            return;
        Speech.Retire(this);
        StopAllSources(); StopMovieAudio();
        StopStationAmbient();
        StopStationSound(); StopClientSounds(); StopStationSound(_interactionSound); StopInteractionOverlaps(); _interactionSounds.Clear();
        StopEffectSounds();
        StopEngineSounds();
        StopMusic();
        _status = _initializationAttempted ? "OpenAL idle · world frame cleared" : "OpenAL idle · not initialized";
    }

    public void Reset()
    {
        if (_disposed)
            return;
        Speech.Retire(this);
        StopAllSources(); StopMovieAudio();
        StopStationAmbient();
        StopStationSound(); StopClientSounds(); StopStationSound(_interactionSound); StopInteractionOverlaps(); _interactionSounds.Clear();
        StopEffectSounds(clearCache:true);
        StopEngineSounds(clearCache:true);
        StopMusic();
        _musicCatalogTask = null;
        DeleteAllBuffers();
        _status = _initializationAttempted ? "OpenAL reset · selected retail asset generation changed" : "OpenAL not requested";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        FocusOutputChanged-=UpdateDiagnosticOutput;
        DeveloperRenderDiagnostics.Changed-=UpdateDiagnosticOutput;

        try
        {
            if (_alc is not null && _context != null)
                _alc.MakeContextCurrent(_context);
            Speech.Retire(this);
            StopAllSources();
            StopMovieAudio();
            StopFrontendSpeech();
            StopStationAmbient();
            StopStationSound(); StopClientSounds(); StopStationSound(_interactionSound); StopInteractionOverlaps(); _interactionSounds.Clear();
            StopEffectSounds(clearCache:true);
            StopEngineSounds(clearCache:true);
            StopMusic();
            DeleteAllBuffers();
        }
        catch
        {
            // App shutdown must not be held hostage by an audio-driver failure.
        }

        if (_alc is not null)
        {
            try
            {
                _alc.MakeContextCurrent(null);
                if (_context != null)
                    _alc.DestroyContext(_context);
                if (_device != null)
                    _alc.CloseDevice(_device);
            }
            catch
            {
                // Best-effort native teardown during app/GL surface shutdown.
            }
        }

        _context = null;
        _device = null;
        _al?.Dispose();
        _alc?.Dispose();
        _al = null;
        _alc = null;
        _musicCancellation.Dispose();
    }

    private bool EnsureInitialized()
    {
        if (Environment.GetEnvironmentVariable("ENB_AUDIO_SILENT") == "1" &&
            !(MemoryOnlyOutput && _al is not null && _alc is not null && _device != null && _context != null))
        {
            _status = "audio silent · output device disabled";
            return false;
        }
        if (_al is not null && _alc is not null && _device != null && _context != null)
            return true;
        if (MemoryOnlyOutput) return false; // Never fall back to a physical output device.
        if (_initializationAttempted)
            return false;

        _initializationAttempted = true;
        try
        {
            _alc = ALContext.GetApi(soft: true);
            _al = AL.GetApi(soft: true);
            _device = _alc.OpenDevice("");
            if (_device == null)
            {
                _status = "OpenAL unavailable · default output device could not be opened";
                return false;
            }

            _context = _alc.CreateContext(_device, null);
            if (_context == null || !_alc.MakeContextCurrent(_context))
            {
                _status = "OpenAL unavailable · output context could not be created/current";
                return false;
            }

            _al.GetError();
            if(focusMuted || !DeveloperRenderDiagnostics.Audio || Environment.GetEnvironmentVariable("ENB_AUDIO_MUTE_OUTPUT")=="1")
                _al.SetListenerProperty(ListenerFloat.Gain,0f);
            // ALC_DEVICE_SPECIFIER can be just "OpenAL Soft"; the enumerate-all
            // extension identifies the Windows endpoint actually opened.
            DeviceName = _alc.GetContextProperty(_device, _alc.IsExtensionPresent(null,"ALC_ENUMERATE_ALL_EXT")
                ? (GetContextString)0x1013 : GetContextString.DeviceSpecifier);
            _status = "OpenAL initialized · default output device";
            return true;
        }
        catch (Exception ex)
        {
            _status = $"OpenAL unavailable · {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private bool TryGetOrCreateBuffer(
        LiveRslSoundRequest request,
        EnbMixResourceCatalog? retailMixResources,
        out OpenAlSoundBuffer buffer,
        out string? diagnostic)
    {
        buffer = default!;
        diagnostic = null;
        if (_al is null)
        {
            diagnostic = "OpenAL API is unavailable.";
            return false;
        }

        string resourceKey = BuildResourceKey(request.Choice);
        if (_buffers.TryGetValue(resourceKey, out OpenAlSoundBuffer? cached))
        {
            buffer = cached;
            return true;
        }

        if (!EnbRetailSoundResourceReader.TryRead(
                request.Choice,
                retailMixResources,
                out byte[] bytes,
                out string source,
                out diagnostic))
        {
            return false;
        }

        if (!EnbRetailWaveAudio.TryDecodePcm16(bytes, out EnbRetailWavePcm16 pcm, out diagnostic))
            return false;
        if (!EnbCurrentRetailRsl3dSoundRuntime.TryValidateDecodedSpatialPcm(pcm, out diagnostic))
            return false;

        uint bufferId = _al.GenBuffer();
        AudioError allocationError = _al.GetError();
        if (bufferId == 0 || allocationError != AudioError.NoError)
        {
            diagnostic = $"OpenAL buffer allocation failed ({allocationError}).";
            return false;
        }

        try
        {
            fixed (byte* sampleData = pcm.Pcm16LittleEndian)
                _al.BufferData(bufferId, BufferFormat.Mono16, sampleData, pcm.Pcm16LittleEndian.Length, pcm.SampleRate);

            AudioError uploadError = _al.GetError();
            if (uploadError != AudioError.NoError)
                throw new InvalidOperationException($"OpenAL buffer upload failed ({uploadError}).");

            buffer = new OpenAlSoundBuffer(
                resourceKey,
                bufferId,
                source,
                pcm.SourceFormat,
                pcm.SampleRate,
                pcm.Channels,
                pcm.FrameCount,
                pcm.DurationSeconds);
            _buffers.Add(resourceKey, buffer);
            return true;
        }
        catch (Exception ex)
        {
            _al.DeleteBuffer(bufferId);
            _al.GetError();
            diagnostic = ex.Message;
            return false;
        }
    }

    private static void ApplyCameraRelativePosition(
        AL al,
        uint source,
        EnbResolvedRenderCamera camera,
        Vector3 worldPosition)
    {
        Vector3 relative = camera.WorldToCanonicalView(worldPosition);
        al.SetSourceProperty(source, SourceVector3.Position, relative.X, relative.Y, relative.Z);
    }

    private void RetireInstance(LiveRslSoundInstanceKey key,bool release=false)
    {
        if (!_instances.Remove(key, out OpenAlSoundInstance? instance) || _al is null)
            return;
        if(release && !instance.Plan.StopsOnCleanup)
        {
            _al.GetSourceProperty(instance.Source,GetSourceInteger.SourceState,out int state);
            if(state==(int)SourceState.Playing){_rslSoundTails.Add(instance);return;}
        }
        _al.SourceStop(instance.Source);
        DeleteVoiceSource(instance.Source);
        _al.GetError();
    }

    private void StopAllSources()
    {
        if ((_instances.Count > 0 || _rslSoundTails.Count>0) && _alc is not null && _context != null)
            _alc.MakeContextCurrent(_context);
        foreach (LiveRslSoundInstanceKey key in _instances.Keys.ToArray())
            RetireInstance(key);
        foreach(var tail in _rslSoundTails)
        { _al!.SourceStop(tail.Source);DeleteVoiceSource(tail.Source); }
        _rslSoundTails.Clear();
    }

    private void DeleteAllBuffers()
    {
        if (_al is not null)
        {
            foreach (OpenAlSoundBuffer buffer in _buffers.Values)
                _al.DeleteBuffer(buffer.BufferId);
            _al.GetError();
        }
        _buffers.Clear();
    }

    private static string BuildResourceKey(EnbRetailSoundResolvedChoice choice)
        => choice.Status switch
        {
            EnbRetailSoundResourceStatus.LooseFile => $"loose:{choice.LoosePath}",
            EnbRetailSoundResourceStatus.MixResource when choice.MixLocation is EnbMixResourceLocation location =>
                $"mix:{location.ArchivePath}:{location.DataOffset}:{location.DataLength}",
            _ => $"unresolved:{choice.Choice.LogicalResourceName}",
        };

    private static string DescribeInstance(
        LiveRslSoundRequest request,
        OpenAlSoundBuffer buffer,
        string disposition,
        EnbResolvedRenderCamera camera,
        Vector3 worldPosition)
    {
        float distance = Vector3.Distance(camera.Position, worldPosition);
        float attenuation = LinearDistanceClampedAttenuation(
            distance,
            request.Plan.ReferenceDistance,
            request.Plan.MaximumDistance);
        return $"{disposition} {request.LogicalSoundName} -> {buffer.Source} · {buffer.SourceFormat} " +
               $"{buffer.Channels}ch {buffer.SampleRate}Hz {buffer.DurationSeconds:0.###}s · " +
               $"gain {request.Plan.Gain:0.###} pitch {request.Plan.Pitch:0.###} " +
               $"radii {request.Plan.ReferenceDistance:0.###}/{request.Plan.MaximumDistance:0.###} " +
               $"distance {distance:0.###} linearAtten {attenuation:0.###} · " +
               $"loop {(request.Plan.LoopContinuously ? "continuous" : "one-pass")} " +
               $"spatial {(request.Plan.FollowsLiveObjectTransform ? "follow" : "stationary")}";
    }

    private static float LinearDistanceClampedAttenuation(float distance, float referenceDistance, float maximumDistance)
    {
        if (!float.IsFinite(distance) || !float.IsFinite(referenceDistance) || !float.IsFinite(maximumDistance) ||
            referenceDistance < 0f || maximumDistance <= referenceDistance)
        {
            return 0f;
        }

        float clampedDistance = Math.Clamp(distance, referenceDistance, maximumDistance);
        return Math.Clamp(1f - ((clampedDistance - referenceDistance) / (maximumDistance - referenceDistance)), 0f, 1f);
    }

    private sealed record OpenAlSoundBuffer(
        string Key,
        uint BufferId,
        string Source,
        string SourceFormat,
        int SampleRate,
        int Channels,
        int FrameCount,
        double DurationSeconds);

    private sealed record OpenAlSoundInstance(
        LiveRslSoundInstanceKey Key,
        string LogicalSoundName,
        uint Source,
        OpenAlSoundBuffer Buffer,
        Vector3 FixedWorldPosition,
        EnbCurrentRetailRsl3dSoundPlan Plan,
        ulong HandleId)
    {
        public Vector3 LastWorldPosition { get; set; }=FixedWorldPosition;
        public int PlaybackOrdinal { get; init; }
    }
}

internal readonly record struct LiveRslSoundInstanceKey(
    uint GameId,
    int SoundStateId,
    uint ActivationClientTime)
{
    public ulong RenderIdentity { get; init; }
    public ulong VisualGeneration { get; init; }
}

internal sealed record LiveRslSoundRequest(
    LiveRslSoundInstanceKey Key,
    string LogicalSoundName,
    EnbRetailSoundResolvedChoice Choice,
    EnbCurrentRetailRsl3dSoundPlan Plan,
    Vector3 WorldPosition)
{
    public ulong HandleId { get; init; }
    public bool UpdateEnabled { get; init; } = true;
    public int PlaybackOrdinal { get; init; }
}

internal readonly record struct LiveRslSoundCompletionEvent(LiveRslSoundInstanceKey Key, uint ObservedClientTime)
{
    public int PlaybackOrdinal { get; init; }
}
