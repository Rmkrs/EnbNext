using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private bool _planetExhaustMode;
    private EnbRetailAssetBundle? _planetExhaustAssets;
    private EnbCurrentRetailShipExhaustAsset[] _planetExhaust = [];

    private void PreparePlanetExhaust(LiveFrame frame, EnbRetailAssetBundle assets)
    {
        _planetExhaustMode = frame.Snapshot.Environment?.PresentationMode == 1 && frame.Movie is null;
        if (!_planetExhaustMode || ReferenceEquals(assets, _planetExhaustAssets)) return;
        _planetExhaustAssets = assets;
        _planetExhaust = new[] { "plnt_eng_p1", "plnt_eng_p2", "ste01p1", "ste01p2" }
            .Select(name =>
            {
                try { return EnbCurrentRetailShipExhaustAsset.Load(assets, name, allowInheritedMotion: true); }
                catch (InvalidDataException) { return null; }
            }).OfType<EnbCurrentRetailShipExhaustAsset>().ToArray();
    }

    // 006FDBD0 -> 006FD7A0: type-1, non-organic ship in client mode 1.
    // Replace every EXHAUST bone's children; keep installed particle parameters.
    private bool UsesPlanetExhaust(IEnbRenderObject obj) => _planetExhaustMode &&
        obj.ObjectType == 1 && obj.ShipPresentation is { IsOrganic: not true, EngineTrailType: not null };

    private void ObservePlanetExhaust(IEnbRenderObject obj, EnbW3dRenderHierarchy hierarchy,
        IReadOnlyList<Matrix4x4> pose, string path)
    {
        if (!UsesPlanetExhaust(obj)) return;
        for (int pivot = 0; pivot < hierarchy.Pivots.Count; pivot++)
        {
            string bone = hierarchy.Pivots[pivot].Name;
            if (!EnbCurrentRetailShipExhaustVisibilityRuntime.IsExhaustBoneName(bone)) continue;
            foreach (var asset in _planetExhaust)
                ObserveShipExhaust(obj, new(asset.Definition.Name, bone, null, true, asset),
                    pose[pivot], hierarchy.Name, pivot, $"{path}/planet-exhaust/{pivot}/{asset.Definition.Name}",
                    allowInheritedMotion: true);
        }
    }
}
