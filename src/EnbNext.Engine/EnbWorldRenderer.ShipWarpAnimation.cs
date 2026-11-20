using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly EnbShipWarpAnimationRuntime _shipWarpAnimation = new();
    private readonly Dictionary<(ulong Owner, string Path), (ulong Sequence, string Name, float Frame)> _shipAnimationSamples = [];
    private readonly List<EnbCurrentRetailEffectSoundNode> _shipAnimationSounds = [];
    private EnbRetailAssetBundle? _shipAnimationAssets;
    private uint _shipAnimationNow;
    private ulong _shipAnimationSoundSequence;
    private int _shipAnimatedParts;

    private void ClearShipWarpAnimations()
    {
        _shipWarpAnimation.Clear(); _shipAnimationSamples.Clear(); _shipAnimationSounds.Clear();
    }

    private void PrepareShipWarpAnimations(IEnbSceneFrame scene, EnbRetailAssetBundle assets, uint now)
    {
        if (!ReferenceEquals(_shipAnimationAssets, assets) || scene.IsTransitioning)
        {
            ClearShipWarpAnimations();
        }
        _shipAnimationAssets = assets; _shipAnimationNow = now; _shipAnimatedParts = 0;
        _shipWarpAnimation.Update(scene.IsTransitioning ? [] : SceneObjects(scene), now);
        var owners = _frameObjectIdentities;
        foreach (var key in _shipAnimationSamples.Keys.Where(k => !owners.Contains(k.Owner)).ToArray()) _shipAnimationSamples.Remove(key);
        _shipAnimationSounds.RemoveAll(n => !owners.Contains(n.Root.InstanceGeneration));
    }

    private LiveRslAnimationBlendBinding? ResolveShipWarpAnimation(IEnbRenderObject obj, LiveCpuRoot root, string path, Vector3 position)
    {
        bool observePlayback = !_preparingLegoRadiusQueries;
        if (_shipWarpAnimation.Get(obj.RenderIdentity) is not { } command || _shipAnimationAssets is not { } assets ||
            root.Resource.HierarchicalLods.Count == 0) return null;
        // 00810540 uses RenderObj's name twice, unlike RSL's hierarchy/suffix naming.
        string name = $"{root.RootRenderObjectName}.{root.RootRenderObjectName}-{command.Suffix}";
        if (!_hAnimRegistry.TryResolve(root.Resource, new("", EnbRslFileDependencyStatus.NotAuthored), assets.Paths,
                assets.MixResources, assets.RetailMixResources, name, out var resolved, out _)) return null;
        float frame = EnbShipWarpAnimationRuntime.Frame(command, _shipAnimationNow, resolved.Animation.FrameRate, checked((int)resolved.Animation.NumFrames));
        var key = (obj.RenderIdentity, path);
        float previous = 0;
        if (_shipAnimationSamples.TryGetValue(key, out var old) && old.Sequence == command.Sequence && old.Name == name) previous = old.Frame;
        if(observePlayback)
        {
            _shipAnimationSamples[key] = (command.Sequence, name, frame);
            _shipAnimatedParts++;
        }
        if (observePlayback && assets.AnimationSounds?.TryGet(name, out var track) == true &&
            (string.IsNullOrEmpty(track.BoneName) || track.BoneName.Equals("root", StringComparison.OrdinalIgnoreCase)))
        {
            // 00909F90: strict previous < authored frame <= current, preserving INI order.
            foreach (var cue in track.Cues.Where(c => previous < c.Frame && c.Frame <= frame))
            {
                var node = EnbWarpSoundRuntime.Node(obj, ++_shipAnimationSoundSequence, 0, cue.Sound, cue.Flat);
                _shipAnimationSounds.Add(node with { ClientWarp = false, ClientAnimation = true, BoundGameId = null,
                    Root = node.Root with { GeometryKind = EnbSceneEffectGeometryKind.Point, WorldPoint = position } });
                if (_shipAnimationSounds.Count > 128) _shipAnimationSounds.RemoveAt(0);
            }
        }
        var binding = new LiveRslAnimationBinding(command.Suffix, name, resolved.Source, command.StartTime,
            resolved.Animation, new(resolved.Animation.Name, frame), resolved.DrawResource);
        return CreateRslAnimationBlend(root, [binding]);
    }
}
