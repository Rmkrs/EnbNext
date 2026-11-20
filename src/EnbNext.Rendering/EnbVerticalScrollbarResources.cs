using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>0066E460 installed vertical slider resources, shared by the asset session.</summary>
public sealed class EnbVerticalScrollbarResources
{
    public readonly record struct Arrow(EnbAuthoredUiAsset Asset, EnbUiRect Bounds);
    public Arrow Up { get; }
    public Arrow Down { get; }
    public EnbAuthoredUiAsset Thumb { get; }

    internal EnbVerticalScrollbarResources(EnbRetailAssetBundle assets)
    {
        Up = Load("slider_up.w3d", "CLICK_UP"); Down = Load("slider_dn.w3d", "CLICK_DOWN");
        var thumb = EnbAuthoredUiAsset.Load(assets, "slider_vert.w3d") ?? throw new InvalidDataException("slider_vert.w3d");
        var catalog = assets.RetailMixResources!;
        if (!catalog.TryReadResource("slider_vert.w3d", out var thumbBytes, out var thumbLocation) ||
            !catalog.TryReadResource("slider_vert-1.w3d", out var resizeBytes, out _) ||
            !EnbW3dModel.TryDecode([..thumbBytes, ..resizeBytes], out var thumbModel) ||
            !EnbAuthoredUiAsset.TryCompile(catalog, thumbLocation, thumbModel, "slider_vert", out var resource))
            throw new InvalidDataException("Retail scrollbar resize animation unavailable.");
        Thumb = thumb with { Resource = resource };

        Arrow Load(string file, string mesh)
        {
            var asset = EnbAuthoredUiAsset.Load(assets, file) ?? throw new InvalidDataException(file);
            return new(asset with { Resource = asset.Resource.Copy().SetMeshes(asset.Resource.Meshes.Where(m => m.Name == mesh).ToArray()) },
                EnbAuthoredUiAsset.MeshBounds(asset, mesh).Translate(-320, -240));
        }
    }
}
