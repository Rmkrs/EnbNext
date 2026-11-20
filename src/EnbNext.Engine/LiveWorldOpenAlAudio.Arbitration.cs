using EnbNext.Rendering;
using Silk.NET.OpenAL;

namespace EnbNext.Engine;

internal sealed unsafe partial class LiveWorldOpenAlAudio
{
    // 009F0350 requires a mono, format-tag-1 buffer for the native 3D bank.
    // Decoding MP3/ADPCM to PCM must not erase that source-format distinction.
    private static bool UsesSpatialBank(EnbNext.Assets.EnbRetailWavePcm16 pcm)
        => pcm.Channels == 1 && pcm.SourceFormat.StartsWith("PCM",StringComparison.Ordinal);
    private readonly EnbRetailSoundArbitration _voiceArbitration = new();
    private readonly EnbRetailSoundArbitration _spatialVoiceArbitration = new();
    private readonly Dictionary<uint, ArbitratedSource> _arbitratedSources = [];
    private sealed class ArbitratedSource(EnbRetailSoundArbitration.Voice voice, EnbRetailSoundArbitration bank)
    {
        public EnbRetailSoundArbitration.Voice Voice = voice;
        public EnbRetailSoundArbitration Bank = bank;
        public float Gain;
    }

    private void RegisterVoice(uint source, string name, int maximum, int type, float priority, float score, uint right = 0, bool spatialMono = false)
    {
        var bank = spatialMono ? _spatialVoiceArbitration : _voiceArbitration;
        var voice = bank.Start(name, maximum, type, priority, score);
        _arbitratedSources.Add(source, new(voice,bank));
        if (right != 0) _arbitratedSources.Add(right, new(voice,bank));
        ApplyVoiceAdmission();
    }

    private void SetVoiceScore(uint source, float score)
    {
        if (_arbitratedSources.TryGetValue(source, out var state)) state.Voice.Score = score;
    }

    private void SetVoiceGain(uint source, float gain)
    {
        if (_arbitratedSources.TryGetValue(source, out var state))
        { state.Gain = gain; gain = state.Voice.Audible ? gain : 0; }
        _al!.SetSourceProperty(source, SourceFloat.Gain, gain);
    }

    private void ApplyVoiceAdmission()
    {
        foreach (var (source, state) in _arbitratedSources)
            _al!.SetSourceProperty(source, SourceFloat.Gain, state.Voice.Audible ? state.Gain : 0);
    }

    public void AdvanceVoiceArbitration()
    {
        Speech.Pump();
        if (_disposed || _al is null || _context == null) return;
        _alc!.MakeContextCurrent(_context);
        foreach (var group in _arbitratedSources.GroupBy(p => p.Value.Voice).ToArray())
        {
            bool finished = true;
            foreach (var (source, _) in group)
            {
                _al.GetSourceProperty(source, GetSourceInteger.SourceState, out int state);
                if (state == (int)SourceState.Playing || state == (int)SourceState.Initial) finished = false;
            }
            if (finished)
            {
                group.First().Value.Bank.Remove(group.Key);
                foreach (var (source, _) in group) _arbitratedSources.Remove(source);
            }
        }
        _voiceArbitration.Recover();
        _spatialVoiceArbitration.Recover();
        ApplyVoiceAdmission();
    }

    private void DeleteVoiceSource(uint source)
    {
        if (_arbitratedSources.Remove(source, out var state) &&
            !_arbitratedSources.Values.Any(v => ReferenceEquals(v.Voice, state.Voice)))
            state.Bank.Remove(state.Voice);
        _al!.DeleteSource(source);
    }
}
