using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed unsafe partial class LiveWorldOpenAlAudio
{
    private readonly Dictionary<(ulong Owner, long Sequence), EmoteAudio> _shipEmoteAudio = [];
    private readonly Dictionary<ulong, long> _shipEmoteAudioSequences = [];
    private void StopShipEmoteAudio()
    {
        foreach (var state in _shipEmoteAudio.Values)
            foreach (var voice in state.Voices) StopStationSound(voice.Sound);
        _shipEmoteAudio.Clear(); _shipEmoteAudioSequences.Clear();
    }
    public void UpdateShipEmotes(IEnbSceneFrame scene, EnbRetailAssetBundle assets,
        EnbResolvedRenderCamera camera, float gain, double clock)
        =>UpdateShipEmotesForObjects(scene.IsTransitioning?[]:scene.Objects,assets,camera,gain,clock);
    public void UpdateShipEmotesForObjects(IReadOnlyList<IEnbRenderObject> objects, EnbRetailAssetBundle assets,
        EnbResolvedRenderCamera camera,float gain,double clock)
    {
        foreach (var obj in objects)
        {
            if (obj.Spatial is not { } spatial) continue;
            if (obj.ShipEmote is { } emote && EnbShipEmoteRuntime.Sound(emote.Emote) is { } preset &&
                _shipEmoteAudioSequences.GetValueOrDefault(obj.RenderIdentity) != emote.Sequence)
            {
                _shipEmoteAudioSequences[obj.RenderIdentity] = emote.Sequence;
                var state = new EmoteAudio();
                _shipEmoteAudio[(obj.RenderIdentity, emote.Sequence)] = state;
                UpdateEmoteAudio(state, emote.Sequence, emote.Seconds, [new(0, preset, obj.ObjectType == 1)],
                    assets, gain, camera, spatial.Transform.Position, clock);
            }
        }
        foreach (var (key, state) in _shipEmoteAudio.ToArray())
        {
            var obj = objects.FirstOrDefault(o => o.RenderIdentity == key.Owner);
            if (obj?.Spatial is { } spatial)
                UpdateEmoteAudio(state, key.Sequence, clock - state.Origin, [], assets, gain, camera, spatial.Transform.Position, clock);
            if (obj?.Spatial is null || state.Voices.Count == 0)
            {
                foreach (var voice in state.Voices) StopStationSound(voice.Sound);
                _shipEmoteAudio.Remove(key);
            }
        }
        foreach (var owner in _shipEmoteAudioSequences.Keys.Where(id => !objects.Any(o => o.RenderIdentity == id)).ToArray())
            _shipEmoteAudioSequences.Remove(owner);
    }
}
