using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using Silk.NET.OpenAL;

namespace EnbNext.Engine;

internal sealed unsafe partial class LiveWorldOpenAlAudio
{
    private sealed class EmoteAudio
    {
        public long Generation;
        public int Next;
        public double Origin;
        public readonly List<(StationSound Sound, string Name, double At, bool Flat)> Voices = [];
    }
    private void UpdateEmoteAudio(EmoteAudio state, long generation, double seconds, IReadOnlyList<EnbRetailAnimationSound> cues,
        EnbRetailAssetBundle assets, float gain, EnbResolvedRenderCamera camera, Vector3 position, double clock)
    {
        if (state.Generation != generation)
        {
            foreach (var voice in state.Voices) StopStationSound(voice.Sound);
            state.Voices.Clear(); state.Next = 0; state.Generation = generation; state.Origin = clock - seconds;
        }
        // Select weighted variants once per cue; each cue gets its own source so claps,
        // cloth and voice can overlap rather than replacing the UI click voice.
        while (state.Next < cues.Count && cues[state.Next].Seconds <= seconds)
        {
            var cue = cues[state.Next++];
            var definition = assets.Sounds!.GetDefinitionOrFile(cue.Preset);
            int total = definition.Files.Sum(f => Math.Max(0, f.Weight ?? 1));
            if (total <= 0) continue;
            int roll = EnbRetailMovieRandom.Damage.Next(1,total)-1;
            var choice = definition.Files.First(f => (roll -= Math.Max(0, f.Weight ?? 1)) < 0);
            if (choice.IsNullChoice) continue;
            string key = cue.Preset + "/" + choice.LogicalResourceName;
            _stationPreparedSounds.GetOrAdd(key, _ => Task.Run(() => DecodeSoundDefinition(definition with { Files = [choice] }, assets)));
            state.Voices.Add((new StationSound { AllowSpeech = true }, key, cue.Seconds, cue.Flat));
        }
        foreach (var voice in state.Voices)
        {
            UpdateStationSound(voice.Sound, voice.Name, assets, gain, Math.Max(0, seconds - voice.At));
            if (voice.Sound.Source != 0 && !voice.Sound.Completed)
            {
                var definition = _effectSoundResources[voice.Name].Load.Result.Definition!;
                _al!.DistanceModel(DistanceModel.LinearDistanceClamped);
                _al.SetSourceProperty(voice.Sound.Source, SourceFloat.RolloffFactor, voice.Flat ? 0f : 1f);
                _al.SetSourceProperty(voice.Sound.Source, SourceFloat.ReferenceDistance, Math.Max(1, definition.EffectiveMaxVolumeRadius));
                _al.SetSourceProperty(voice.Sound.Source, SourceFloat.MaxDistance, definition.EffectiveDropOffRadius);
                if (!voice.Flat) ApplyCameraRelativePosition(_al, voice.Sound.Source, camera, position);
            }
        }
        foreach (var voice in state.Voices.Where(v => v.Sound.Completed).ToArray())
        { StopStationSound(voice.Sound); state.Voices.Remove(voice); }
    }
}

