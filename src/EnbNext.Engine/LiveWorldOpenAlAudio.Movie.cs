using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using Silk.NET.OpenAL;

namespace EnbNext.Engine;

internal sealed unsafe partial class LiveWorldOpenAlAudio
{
    private readonly Dictionary<long, StationSound> _movieSounds = [];
    public void UpdateMovieAudio(EnbRetailMovieFrame frame, EnbRetailAssetBundle assets, float sfxGain)
    {
        foreach (var request in frame.Sounds)
        {
            if (!_movieSounds.TryGetValue(request.Id, out var voice))
            {
                voice = new() { Name = request.Name, AllowSpeech = true, UseAuthoredLoopCount = true,
                    LoopCountOverride = request.Loop ? 0 : null, VolumeOverride = request.Music ? null : request.Volume };
                if (assets.Sounds is { } sounds && sounds.GetDefinitionOrFile(request.Name).EffectiveSoundType != 2 &&
                    !_stationSoundCalls.TryCall(sounds.GetDefinitionOrFile(request.Name)))
                    voice.Completed = true;
                _movieSounds.Add(request.Id, voice);
            }
            if (request.Stopped) { StopStationSound(voice); voice.Completed = true; continue; }
            // 411B99 -> 43F2D0: argument 2 changes reference ownership, not
            // the sound bus. The definition retains its authored gain.
            UpdateStationSound(voice, request.Name, assets, sfxGain);
            if (voice.Source == 0 || _al is null || !request.Attached) continue;
            Vector3 position = request.Position ?? Vector3.Zero;
            ApplyCameraRelativePosition(_al, voice.Source, frame.Camera, position);
            if (assets.Sounds?.GetDefinitionOrFile(request.Name) is { } definition)
            {
                _al.SetSourceProperty(voice.Source, SourceFloat.ReferenceDistance, definition.EffectiveMaxVolumeRadius);
                _al.SetSourceProperty(voice.Source, SourceFloat.MaxDistance, definition.EffectiveDropOffRadius);
                _al.SetSourceProperty(voice.Source, SourceFloat.RolloffFactor, 1);
            }
        }
    }
    private void StopMovieAudio()
    {
        foreach (var voice in _movieSounds.Values) StopStationSound(voice);
        _movieSounds.Clear();
    }
}
