using System.Numerics;
using System.Collections.Immutable;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>005732A0 supplies the size/split animations to 00989BC0 with
/// 009314F0 channel-presence masks and unit weights, without 00931970 normalization.</summary>
public sealed class EnbRetailChatLayoutPose
{
    public static float AdvanceComboFrame(float frame,uint elapsed,bool opening)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        float step=(float)((EnbRetailLineArithmetic.Register)(double)elapsed*BitConverter.UInt32BitsToSingle(0x3a83126f)*50);
        return (float)(opening?(EnbRetailLineArithmetic.Register)frame+step:(EnbRetailLineArithmetic.Register)frame-step);
    }
    private readonly EnbW3dRenderHierarchy _hierarchy;
    private readonly EnbW3dRenderHierarchyAnimation[] _animations;
    private readonly HashSet<int>[] _masks;
    private readonly bool _masked;

    public EnbRetailChatLayoutPose(EnbRetailChatLayout layout)
        : this(layout.Hierarchy, layout.SizeAnimation, layout.SplitAnimation) { }

    public EnbRetailChatLayoutPose(EnbW3dHierarchy hierarchy, EnbW3dHierarchyAnimation size, EnbW3dHierarchyAnimation split, bool masked = true)
    {
        _masked = masked;
        if (!EnbW3dRenderResourceCompiler.TryCompileHierarchy(hierarchy, out _hierarchy, out var error))
            throw new InvalidDataException(error);
        EnbW3dHierarchyAnimation[] source = [size, split];
        _animations = new EnbW3dRenderHierarchyAnimation[2];
        _masks = new HashSet<int>[2];
        for (int i = 0; i < source.Length; i++)
        {
            if (!EnbW3dRenderResourceCompiler.TryCompileAnimation(i, source[i], out _animations[i], out error))
                throw new InvalidDataException(error);
            _masks[i] = source[i].Channels.Select(c => (int)c.PivotIndex)
                .Concat(source[i].BitChannels.Select(c => (int)c.PivotIndex)).ToHashSet();
        }
    }

    public IReadOnlyDictionary<string, Vector3> Evaluate(int sizeFrame, int? splitFrame)
    {
        var pose = EvaluatePose(sizeFrame, splitFrame);
        return _hierarchy.Pivots.ToDictionary(p => p.Name, p => pose.WorldTransforms[p.Ordinal].Translation, StringComparer.Ordinal);
    }

    // 005790C0's Combo01 controller uses the same unit-weight aggregate,
    // but 00931660 leaves both entry masks null. Keep fractional opening frames.
    public EnbW3dHierarchyPoseOwner EvaluatePose(float sizeFrame, float? splitFrame)
    {
        var world = new Matrix4x4[_hierarchy.Pivots.Count];
        world[0] = Matrix4x4.Identity;
        for (int pivot = 1; pivot < world.Length; pivot++)
        {
            var authored = _hierarchy.Pivots[pivot];
            var accumulated = new EnbCurrentRetailHierarchyAnimationAccumulator();
            for (int entry = 0; entry < (splitFrame.HasValue ? 2 : 1); entry++)
            {
                if (_masked && !_masks[entry].Contains(pivot)) continue;
                if (!EnbW3dAnimationSampler.TrySamplePivot(_animations[entry], pivot,
                    entry == 0 ? sizeFrame : splitFrame!.Value, out var sample, out var error))
                    throw new InvalidDataException(error);
                accumulated.AddTranslation(sample.Translation, 1, _hierarchy.CurrentRetailAnimationTranslationScale);
                accumulated.AddRotation(sample.Rotation, 1, EnbW3dAnimationSampler.InterpolateQuaternionRetail);
            }
            world[pivot] = accumulated.Apply(EnbRetailParticleHierarchyTransform.Compose(
                authored.LocalTransform, world[(int)authored.ParentIndex]));
        }
        return new(world, Enumerable.Repeat(true,world.Length).ToArray(),null,null);
    }
}
