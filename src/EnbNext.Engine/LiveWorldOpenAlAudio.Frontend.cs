using EnbNext.Assets;
using Silk.NET.OpenAL;

namespace EnbNext.Engine;

internal sealed unsafe partial class LiveWorldOpenAlAudio
{
    private readonly StationSound _frontendSpeech = new() { AllowSpeech = true, ForceSpeechFile = true };
    private long _frontendSpeechIdentity;
    private long? _frontendSilentStarted;
    public void UpdateFrontendMusic(bool enabled, EnbRetailAssetBundle assets, float gain)
    {
        if (!enabled) { StopMusic(); return; }
        try
        {
            // InterfaceContextInitialize owns the continuous frontend track,
            // independent of the world's music-theme playlist.
            var preset = assets.Sounds?.GetDefinitionOrFile("fe_music.mp3")
                ?? throw new InvalidDataException("Frontend music preset unavailable.");
            string resource = preset.Files.Single(f => !f.IsNullChoice).LogicalResourceName!;
            _musicDecode ??= DecodeMusicAsync(assets, new(resource, 0, 0), _musicCancellation.Token);
            if (!_musicDecode.IsCompleted) return;
            var decoded = _musicDecode.GetAwaiter().GetResult();
            if (decoded.Error is not null) { _musicStatus = "frontend music blocked · " + decoded.Error; return; }
            _musicStatus = $"frontend music decoded {resource}: {decoded.Pcm.Length} PCM bytes";
            if (!EnsureInitialized()) return;
            if (!_alc!.MakeContextCurrent(_context)) return;
            if (_musicSource == 0)
            {
                _playingMusic = decoded;
                _musicStream.UploadedBytes = _musicStream.ConsumedBytes = 0;
                _musicSource = _al!.GenSource();
                for (int i = 0; i < 4; i++)
                {
                    uint buffer = _al.GenBuffer(); _musicBuffers.Add(buffer, 0); QueueMusicBlock(buffer);
                }
                _al.SetSourceProperty(_musicSource, SourceBoolean.SourceRelative, true);
                _al.SetSourceProperty(_musicSource, SourceVector3.Position, 0, 0, 0);
                _al.SetSourceProperty(_musicSource, SourceFloat.RolloffFactor, 0);
            }
            RefillMusic();
            _al!.GetSourceProperty(_musicSource, GetSourceInteger.BuffersQueued, out int queued);
            if (queued == 0)
            {
                _musicStream.UploadedBytes = _musicStream.ConsumedBytes = 0;
                foreach (uint buffer in _musicBuffers.Keys.ToArray()) QueueMusicBlock(buffer);
            }
            _al.SetSourceProperty(_musicSource, SourceFloat.Gain, Math.Clamp(gain * preset.EffectiveVolume, 0, 1));
            _al.GetSourceProperty(_musicSource, GetSourceInteger.SourceState, out int state);
            if (state != (int)SourceState.Playing) _al.SourcePlay(_musicSource);
            CheckMusicError("frontend stream");
            _musicStatus = "frontend music playing fe_music.mp3";
        }
        catch (Exception ex) { _musicStatus = "frontend music blocked · " + ex.Message; RetireMusicSource(); }
    }
    public (bool Complete, double Seconds, string Status) UpdateFrontendSpeech(long identity, string name, EnbRetailAssetBundle assets, float gain)
    {
        if (identity != _frontendSpeechIdentity)
        {
            Speech.Stop();
            StopStationSound(_frontendSpeech);
            _frontendSpeechIdentity = identity;
            _frontendSilentStarted = null;
        }
        UpdateStationSound(_frontendSpeech, name, assets, gain);
        double seconds = 0;
        // Silent validation and machines without an output device still advance
        // the authored speech timeline. Never make login depend on audio hardware.
        if (!_frontendSpeech.Completed && _frontendSpeech.Source == 0 &&
            GetEffectResource(name, assets, true)?.Load is { IsCompletedSuccessfully: true } load &&
            load.Result.Pcm is {} pcm && !EnsureInitialized())
        {
            _frontendSilentStarted ??= Environment.TickCount64;
            seconds = (Environment.TickCount64 - _frontendSilentStarted.Value) / 1000d;
            double duration = pcm.Pcm16LittleEndian.Length / (double)(pcm.SampleRate * pcm.Channels * 2);
            return (seconds >= duration, Math.Min(seconds, duration), _frontendSpeech.Status);
        }
        if (_frontendSpeech.Source != 0 && _al is not null)
        {
            _al.GetSourceProperty(_frontendSpeech.Source, SourceFloat.SecOffset, out float offset);
            seconds = offset;
        }
        return (_frontendSpeech.Completed, seconds, _frontendSpeech.Status);
    }
    public void StopFrontendSpeech()
    {
        if (_frontendSpeechIdentity != 0) Speech.Stop();
        _frontendSpeechIdentity = 0;
        StopStationSound(_frontendSpeech);
    }
}
