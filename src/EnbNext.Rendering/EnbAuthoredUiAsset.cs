using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public sealed record EnbAuthoredUiAsset(EnbW3dRenderResource Resource, EnbW3dHierarchy Hierarchy)
{
    public EnbW3dDeformedGeometry? Geometry { get; init; }
    public static EnbAuthoredUiAsset? Load(EnbRetailAssetBundle assets, string name, bool allowStandalone = false)
    {
        if (assets.RetailMixResources is not { } resources ||
            !resources.TryReadResource(name, out var bytes, out var location) ||
            !EnbW3dModel.TryDecode(bytes, out var model)) return null;
        var hierarchy=model.Hierarchies.FirstOrDefault();
        if(hierarchy is null && (!allowStandalone || model.Meshes.Count==0))return null;
        // Standalone overlays have no pivots; the normal cockpit uses identity too.
        hierarchy ??=new(0,model.Meshes[0].Name,Vector3.Zero,[],[]);
        if(!TryCompile(resources,location,model,hierarchy.Name,out var resource))return null;
        return new(resource, hierarchy);
    }

    public static bool TryCompile(
        EnbMixResourceCatalog resources,
        EnbMixResourceLocation location,
        EnbW3dModel model,
        string requestedObjectName,
        out EnbW3dRenderResource renderResource)
    {
        EnbResolvedTextureReference[] textures = model.Meshes
            .SelectMany(static mesh => mesh.TextureNames)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new EnbResolvedTextureReference(
                name,
                resources.TryLocateResource(name, out EnbMixResourceLocation textureLocation)
                    ? textureLocation
                    : null))
            .ToArray();
        var source = new EnbResolvedW3dResource(
            location.Name,
            requestedObjectName,
            location,
            model,
            textures);
        return EnbW3dRenderResourceCompiler.TryCompile(source, out renderResource, out _);
    }

    public static EnbUiRect MeshBounds(EnbAuthoredUiAsset asset, string name)
    {
        var mesh = asset.Resource.Meshes.First(m => m.Name == name);
        var transform = asset.Hierarchy.Pivots.Count==0?Matrix4x4.Identity:
            ResolveTransforms(asset)[asset.Hierarchy.Pivots.First(p=>p.Name==name).Ordinal];
        var positions = mesh.Positions.Select(p => Vector3.Transform(p, transform)).ToArray();
        Vector3 min = positions.Aggregate(Vector3.Min), max = positions.Aggregate(Vector3.Max);
        return new(min.X + 320, 240 - max.Y, max.X - min.X, max.Y - min.Y);
    }

    public static Matrix4x4[] ResolveTransforms(EnbAuthoredUiAsset asset)
    {
        var hierarchy = asset.Resource.Hierarchies.First(h => h.Name == asset.Hierarchy.Name);
        if (!EnbW3dHierarchyPose.TryEvaluate(hierarchy, Matrix4x4.Identity, out var transforms, out var error))
            throw new InvalidDataException("Cockpit hierarchy: " + error);
        return transforms;
    }


    // Translation-only authored controls retain their original exact path. Rotated
    // feedback meshes use the full hierarchy pose, as in cockpit rendering.
    public Matrix4x4 ModelTransform(string meshName)
    {
        if(Hierarchy.Pivots.Count==0)return Matrix4x4.Identity;
        int ordinal=Hierarchy.Pivots.First(p=>p.Name==meshName).Ordinal;
        return EnbAuthoredTargetPanelLoader.TryResolveTranslation(Hierarchy,ordinal,out var translation)
            ?Matrix4x4.CreateTranslation(translation):ResolveTransforms(this)[ordinal];
    }
    public IEnumerable<float> ModelDepths()
    {
        if(Geometry is {} geometry)return geometry.Positions.Select(p=>p.Z);
        if(Hierarchy.Pivots.Count==0)return Resource.Meshes.SelectMany(mesh=>mesh.Positions.Select(p=>p.Z));
        return Resource.Meshes.SelectMany(mesh=>
        {
            int ordinal=Hierarchy.Pivots.First(p=>p.Name==mesh.Name).Ordinal;
            if(EnbAuthoredTargetPanelLoader.TryResolveTranslation(Hierarchy,ordinal,out _))return mesh.Positions.Select(p=>p.Z);
            var transform=ResolveTransforms(this)[ordinal];
            return mesh.Positions.Select(p=>Vector3.Transform(p,transform).Z);
        });
    }
}
