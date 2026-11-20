using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>One slider's mutable thumb, consuming the 00670300 authored resize animation.</summary>
internal sealed class EnbVerticalScrollbarVisual(EnbVerticalScrollbarResources resources)
{
    public EnbVerticalScrollbarResources Resources => resources;
    public EnbAuthoredUiAsset Thumb { get; private set; } = resources.Thumb;
    public EnbUiRect ThumbBounds { get; private set; }
    private double height = double.NaN;

    public void Resize(double nextHeight)
    {
        if (height == nextHeight) return;
        var thumb = resources.Thumb;
        var animation = thumb.Resource.Animations.Single(a => a.Name.Equals("slider_vert-1", StringComparison.OrdinalIgnoreCase));
        var hierarchy = thumb.Resource.Hierarchies.Single();
        var transforms = new Matrix4x4[hierarchy.Pivots.Count];
        foreach (var pivot in hierarchy.Pivots)
        {
            if (!EnbW3dAnimationSampler.TrySamplePivot(animation, pivot.Ordinal, (float)Math.Max(0, nextHeight - 16), out var sample, out var error))
                throw new InvalidDataException(error);
            var local = Matrix4x4.CreateTranslation(sample.Translation) * pivot.LocalTransform;
            transforms[pivot.Ordinal] = pivot.IsRoot ? local : local * transforms[pivot.ParentIndex];
        }
        var pose = new EnbW3dHierarchyPoseOwner(transforms.ToImmutableArray(), Enumerable.Repeat(true, transforms.Length).ToImmutableArray(), null, null);
        if (!EnbW3dSkinDeformer.TryDeform(thumb.Resource.Meshes.Single(), pose, out var geometry, out var diagnostic))
            throw new InvalidDataException(diagnostic);
        Thumb = thumb with { Geometry = geometry };
        var min = geometry.Positions.Aggregate(Vector3.Min); var max = geometry.Positions.Aggregate(Vector3.Max);
        ThumbBounds = new(min.X, -max.Y, max.X - min.X, max.Y - min.Y);
        height = nextHeight;
    }
}
