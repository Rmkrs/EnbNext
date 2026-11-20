using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Rendering;

public sealed record EnbAuthoredTooltipFrame(EnbW3dRenderResource Resource,
    EnbRetailScannerTooltipDefinition Definition)
{
    public static EnbAuthoredTooltipFrame? Load(EnbRetailAssetBundle assets, EnbRetailScannerTooltipDefinition definition)
    {
        var catalog = assets.RetailMixResources;
        if (catalog is null || string.IsNullOrWhiteSpace(definition.MeshFile)) return null;
        // Get_HAnim uses the authored animation suffix as its lazy W3D name.
        string x = definition.AnimationX.Split('.').Last(), y = definition.AnimationY.Split('.').Last();
        if (!catalog.TryReadResource(definition.MeshFile+".w3d",out var frame,out var location) ||
            !catalog.TryReadResource(x+".w3d",out var animationX,out _) ||
            !catalog.TryReadResource(y+".w3d",out var animationY,out _)) return null;
        byte[] combined = [..frame,..animationX,..animationY];
        return EnbW3dModel.TryDecode(combined,out var model) &&
            EnbAuthoredUiAsset.TryCompile(catalog,location,model,definition.MeshFile,out var resource)
            ? new(resource,definition) : null;
    }

    public EnbW3dDeformedGeometry Geometry(float width, float height, bool compactVertical = false)
    {
        // 00516E00 truncates the clamped UI dimensions and subtracts MinX/Y
        // to select the two resize animation frames (not elapsed-time frames).
        float fx = (int)Math.Clamp(width,Definition.MinWidth,Definition.MaxWidth)-Definition.MinWidth;
        float fy = (int)Math.Clamp(height,Definition.MinHeight,Definition.MaxHeight)-Definition.MinHeight;
        var ax = Resource.Animations.Single(a => a.Name.Equals(Definition.AnimationX.Split('.').Last(),StringComparison.OrdinalIgnoreCase));
        var ay = Resource.Animations.Single(a => a.Name.Equals(Definition.AnimationY.Split('.').Last(),StringComparison.OrdinalIgnoreCase));
        var hierarchy = Resource.Hierarchies.Single();
        var transforms = new Matrix4x4[hierarchy.Pivots.Count];
        foreach (var pivot in hierarchy.Pivots)
        {
            if (!EnbW3dAnimationSampler.TrySamplePivot(ax,pivot.Ordinal,fx,out var sx,out var error) ||
                !EnbW3dAnimationSampler.TrySamplePivot(ay,pivot.Ordinal,fy,out var sy,out error))
                throw new InvalidOperationException(error);
            var local = Matrix4x4.CreateTranslation(sx.Translation+sy.Translation)*pivot.LocalTransform;
            transforms[pivot.Ordinal] = pivot.IsRoot ? local : local*transforms[pivot.ParentIndex];
        }
        var pose = new EnbW3dHierarchyPoseOwner(transforms.ToImmutableArray(),
            Enumerable.Repeat(true,transforms.Length).ToImmutableArray(),null,null);
        if (!EnbW3dSkinDeformer.TryDeform(Resource.Meshes.Single(),pose,out var geometry,out var diagnostic))
            throw new InvalidOperationException(diagnostic);
        if (compactVertical)
        {
            // The fixed two-line map strip preserves the texture's vertical
            // proportions rather than squeezing its centre between two bevels.
            var uv = Resource.Meshes.Single().MaterialPasses.Single().TextureStages[0].RuntimeTexCoords;
            float top = geometry.Positions.Max(p => p.Y), bottom = geometry.Positions.Min(p => p.Y);
            float first = uv.Min(p => p.Y), last = uv.Max(p => p.Y);
            if (last > first)
                geometry = geometry with { Positions = geometry.Positions.Select((p, i) =>
                    new Vector3(p.X, top - (uv[i].Y - first) / (last - first) * (top - bottom), p.Z)).ToArray() };
        }
        return geometry;
    }
}
