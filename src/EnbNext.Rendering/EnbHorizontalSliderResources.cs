using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>0066E460 horizontal slider resources, owned by the retail asset session.</summary>
public sealed class EnbHorizontalSliderResources
{
    public EnbAuthoredUiAsset Left { get; }
    public EnbAuthoredUiAsset Right { get; }
    public EnbAuthoredUiAsset Thumb { get; }
    public EnbUiRect ThumbBounds { get; }

    internal EnbHorizontalSliderResources(EnbRetailAssetBundle assets)
    {
        Left = EnbAuthoredUiAsset.Load(assets, "slider_lt.w3d") ?? throw new InvalidDataException("slider_lt.w3d unavailable.");
        Right = EnbAuthoredUiAsset.Load(assets, "slider_rt.w3d") ?? throw new InvalidDataException("slider_rt.w3d unavailable.");
        var source = EnbAuthoredUiAsset.Load(assets, "slider_hor.w3d") ?? throw new InvalidDataException("slider_hor.w3d unavailable.");
        var catalog = assets.RetailMixResources!;
        if (!catalog.TryReadResource("slider_hor.w3d", out var bytes, out var location) ||
            !catalog.TryReadResource("slider_hor-1.w3d", out var resize, out _) ||
            !EnbW3dModel.TryDecode([.. bytes, .. resize], out var model) ||
            !EnbAuthoredUiAsset.TryCompile(catalog, location, model, "slider_hor", out var resource))
            throw new InvalidDataException("Retail horizontal slider animation unavailable.");
        (Thumb, ThumbBounds) = Sample(source with { Resource = resource }, 16);
    }

    public (EnbAuthoredUiAsset Asset, EnbUiRect Bounds) Sample(double length)
        => length == 16 ? (Thumb, ThumbBounds) : Sample(Thumb, length);

    private static (EnbAuthoredUiAsset Asset, EnbUiRect Bounds) Sample(EnbAuthoredUiAsset source, double length)
    {
        var hierarchy = source.Resource.Hierarchies.Single();
        var animation = source.Resource.Animations.Single(a => a.Name.Equals("slider_hor-1", StringComparison.OrdinalIgnoreCase));
        var matrices = new Matrix4x4[hierarchy.Pivots.Count];
        foreach (var pivot in hierarchy.Pivots)
        {
            if (!EnbW3dAnimationSampler.TrySamplePivot(animation, pivot.Ordinal, (float)Math.Max(0, length - 16), out var sample, out var error))
                throw new InvalidDataException(error);
            var local = Matrix4x4.CreateTranslation(sample.Translation) * pivot.LocalTransform;
            matrices[pivot.Ordinal] = pivot.IsRoot ? local : local * matrices[pivot.ParentIndex];
        }
        var pose = new EnbW3dHierarchyPoseOwner(matrices.ToImmutableArray(), Enumerable.Repeat(true, matrices.Length).ToImmutableArray(), null, null);
        if (!EnbW3dSkinDeformer.TryDeform(source.Resource.Meshes.Single(), pose, out var geometry, out var diagnostic))
            throw new InvalidDataException(diagnostic);
        var min = geometry.Positions.Aggregate(Vector3.Min); var max = geometry.Positions.Aggregate(Vector3.Max);
        return (source with { Geometry = geometry }, new(min.X, -max.Y, max.X - min.X, max.Y - min.Y));
    }
}
