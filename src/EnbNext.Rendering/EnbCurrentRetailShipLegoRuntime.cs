using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>
/// Current-retail ship Lego attachment and hierarchy-state semantics.
///
/// Retail keeps BaseAsset scale out of the RenderObject root transform. The
/// scale is applied to mesh geometry and to hierarchy-pivot translations while
/// the pivot rotation bases remain rigid. Lego attachment does not assign a
/// separately composed world matrix to the child controller. It enables the
/// selected parent hierarchy state record, installs the child controller's
/// stored ATTACH inverse as that record's mode-0 override, and attaches the
/// child RenderObject to the record. Hierarchy evaluation applies the override
/// after the selected pivot's authored/animated transform and before evaluating
/// descendants. The attached child's own transform is then assigned directly
/// from the evaluated parent record.
/// </summary>
public static class EnbCurrentRetailShipLegoRuntime
{
    public const string AttachTransformName = "ATTACH";

    // FUN_00729FC0 initializes every Lego controller at 1.0. The root
    // ShipAuxData.Legos.Scale notification at FUN_00732790 dispatches through
    // FUN_0072A5E0, which does not replace that controller field. Child
    // RenderObjects are therefore created, and their ATTACH inverse captured,
    // at this scale. HLOD attachment copies LOD bias, not geometry scale.
    public const float LegoControllerCreationScale = 1f;

    /// <summary>
    /// Final current-retail mode-0 override state for one hierarchy record.
    /// The retail record stores one matrix per pivot; a later write to the same
    /// pivot replaces the earlier matrix.
    /// </summary>
    public readonly record struct HierarchyPivotOverride(int PivotIndex, Matrix4x4 Transform);

    public static bool TryBuildRigidObjectWorld(
        IEnbRenderObject obj,
        out Matrix4x4 rigidWorld,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(obj);
        rigidWorld = default;
        diagnostic = null;

        if (obj.Spatial?.Transform is not IEnbRenderTransform transform)
        {
            diagnostic = $"Ship Lego object 0x{obj.GameId:X8} has no materialized spatial transform.";
            return false;
        }
        if (!IsFinite(transform.Position) || !IsFinite(transform.Orientation))
        {
            diagnostic = $"Ship Lego object 0x{obj.GameId:X8} has a non-finite spatial transform.";
            return false;
        }

        if (!EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(transform,
                out rigidWorld,
                out string? transformDiagnostic))
        {
            diagnostic = $"Ship Lego object 0x{obj.GameId:X8} could not build its current-retail rigid world transform: {transformDiagnostic}";
            return false;
        }

        return true;
    }

    public static bool TryEvaluateScaledRigidHierarchy(
        EnbW3dRenderHierarchy hierarchy,
        Matrix4x4 rigidRootWorld,
        float baseAssetScale,
        out Matrix4x4[] worldTransforms,
        out string? diagnostic)
        => TryEvaluateScaledRigidHierarchy(
            hierarchy,
            rigidRootWorld,
            baseAssetScale,
            animations: null,
            hierarchyOverrides: null,
            out worldTransforms,
            out diagnostic);

    /// <summary>
    /// Current-retail RSL wrappers delegate hierarchy queries to their retained
    /// current RenderObject. If that object is animated, attached children follow
    /// the same live hierarchy pose. BaseAsset scale mutates authored pivot
    /// translations before HAnim evaluation; animation-channel translations remain
    /// the authored HAnim values.
    /// </summary>
    public static bool TryEvaluateScaledRigidHierarchy(
        EnbW3dRenderHierarchy hierarchy,
        Matrix4x4 rigidRootWorld,
        float baseAssetScale,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample>? animations,
        out Matrix4x4[] worldTransforms,
        out string? diagnostic)
        => TryEvaluateScaledRigidHierarchy(
            hierarchy,
            rigidRootWorld,
            baseAssetScale,
            animations,
            hierarchyOverrides: null,
            out worldTransforms,
            out diagnostic);

    /// <summary>
    /// Evaluates the rigid hierarchy state records, including the final mode-0
    /// overrides installed by Lego attachment. System.Numerics is row-vector,
    /// so retail's column-vector currentWorld * override becomes
    /// override * currentWorld here.
    /// </summary>
    public static bool TryEvaluateScaledRigidHierarchy(
        EnbW3dRenderHierarchy hierarchy,
        Matrix4x4 rigidRootWorld,
        float baseAssetScale,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample>? animations,
        IReadOnlyCollection<HierarchyPivotOverride>? hierarchyOverrides,
        out Matrix4x4[] worldTransforms,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        worldTransforms = [];
        diagnostic = null;

        Dictionary<int, Matrix4x4>? overridesByPivot = null;
        if (hierarchyOverrides is { Count: > 0 })
        {
            overridesByPivot = new Dictionary<int, Matrix4x4>();
            foreach (HierarchyPivotOverride hierarchyOverride in hierarchyOverrides)
            {
                if (hierarchyOverride.PivotIndex < 0 || hierarchyOverride.PivotIndex >= hierarchy.Pivots.Count)
                {
                    diagnostic = $"Hierarchy '{hierarchy.Name}' override pivot {hierarchyOverride.PivotIndex} is outside 0..{hierarchy.Pivots.Count - 1}.";
                    return false;
                }
                if (!IsFinite(hierarchyOverride.Transform))
                {
                    diagnostic = $"Hierarchy '{hierarchy.Name}' override pivot {hierarchyOverride.PivotIndex} has a non-finite mode-0 transform.";
                    return false;
                }
                if (!overridesByPivot.TryAdd(hierarchyOverride.PivotIndex, hierarchyOverride.Transform))
                {
                    diagnostic = $"Hierarchy '{hierarchy.Name}' received more than one final override for pivot {hierarchyOverride.PivotIndex}.";
                    return false;
                }
            }
        }

        if (!TryEvaluateScaledRigidHierarchyPose(
                hierarchy,
                rigidRootWorld,
                baseAssetScale,
                animations,
                overridesByPivot,
                out EnbW3dHierarchyPoseOwner pose,
                out diagnostic))
        {
            return false;
        }

        worldTransforms = pose.WorldTransforms;
        return true;
    }

    /// <summary>
    /// Pose-returning companion used when the final state records must drive
    /// mesh/skin submission as well as attachment queries. Visibility remains
    /// part of the returned retail hierarchy pose.
    /// </summary>
    public static bool TryEvaluateScaledRigidHierarchyPose(
        EnbW3dRenderHierarchy hierarchy,
        Matrix4x4 rigidRootWorld,
        float baseAssetScale,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample>? animations,
        IReadOnlyDictionary<int, Matrix4x4>? hierarchyOverrides,
        out EnbW3dHierarchyPoseOwner pose,
        out string? diagnostic, EnbW3dHierarchyPoseOwner? storage=null)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        pose = default!;
        diagnostic = null;

        if (!IsFinite(rigidRootWorld))
        {
            diagnostic = $"Hierarchy '{hierarchy.Name}' received a non-finite rigid root transform.";
            return false;
        }
        if (!float.IsFinite(baseAssetScale))
        {
            diagnostic = $"Hierarchy '{hierarchy.Name}' received non-finite BaseAsset scale {baseAssetScale}.";
            return false;
        }
        if (hierarchy.Pivots.Count == 0)
        {
            diagnostic = $"Hierarchy '{hierarchy.Name}' contains no pivots.";
            return false;
        }
        if (!hierarchy.Pivots[0].IsRoot)
        {
            diagnostic = $"Hierarchy '{hierarchy.Name}' pivot 0 is not an authored root.";
            return false;
        }
        if (hierarchyOverrides is not null)
        {
            foreach ((int pivotIndex, Matrix4x4 transform) in hierarchyOverrides)
            {
                if (pivotIndex < 0 || pivotIndex >= hierarchy.Pivots.Count)
                {
                    diagnostic = $"Hierarchy '{hierarchy.Name}' override pivot {pivotIndex} is outside 0..{hierarchy.Pivots.Count - 1}.";
                    return false;
                }
                if (!IsFinite(transform))
                {
                    diagnostic = $"Hierarchy '{hierarchy.Name}' override pivot {pivotIndex} has a non-finite mode-0 transform.";
                    return false;
                }
            }
        }

        bool evaluated = animations is { Count: > 0 }
            ? EnbW3dHierarchyPose.TryEvaluate(
                hierarchy,rigidRootWorld,animations,hierarchyOverrides,
                out pose,out diagnostic,storedTranslationScale:baseAssetScale,storage:storage)
            : EnbW3dHierarchyPose.TryEvaluate(
                hierarchy,rigidRootWorld,animation:null,frame:0f,postWorldTransforms:hierarchyOverrides,
                out pose,out diagnostic,storedTranslationScale:baseAssetScale,storage:storage);
        return evaluated;
    }

    /// <summary>
    /// Current RenderObject hierarchy name lookup is case-insensitive and the
    /// retail resolver falls back to hierarchy entry 0 when a name is absent.
    /// 0098A000 walks records in order and returns the first matching name.
    /// </summary>
    public static bool TryResolveRetailHierarchyPivot(
        EnbW3dRenderHierarchy hierarchy,
        string name,
        out int pivotIndex,
        out bool usedRetailMissingNameFallback,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        ArgumentNullException.ThrowIfNull(name);
        pivotIndex = 0;
        usedRetailMissingNameFallback = false;
        diagnostic = null;

        int matched = -1;
        for (int index = 0; index < hierarchy.Pivots.Count; index++)
        {
            if (!string.Equals(hierarchy.Pivots[index].Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            matched = index;
            break;
        }

        if (matched >= 0)
        {
            pivotIndex = matched;
            return true;
        }

        if (hierarchy.Pivots.Count == 0)
        {
            diagnostic = $"Hierarchy '{hierarchy.Name}' contains no pivots.";
            return false;
        }

        pivotIndex = 0;
        usedRetailMissingNameFallback = true;
        return true;
    }

    public static bool TryResolveParentMountWorld(
        EnbW3dRenderHierarchy parentHierarchy,
        Matrix4x4 parentRigidWorld,
        float parentScale,
        EnbClientBaseAssetMount mount,
        out Matrix4x4 parentMountWorld,
        out string? diagnostic)
        => TryResolveParentMountWorld(
            parentHierarchy,
            parentRigidWorld,
            parentScale,
            mount,
            animations: null,
            hierarchyOverrides: null,
            out parentMountWorld,
            out diagnostic);

    public static bool TryResolveParentMountWorld(
        EnbW3dRenderHierarchy parentHierarchy,
        Matrix4x4 parentRigidWorld,
        float parentScale,
        EnbClientBaseAssetMount mount,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample>? animations,
        out Matrix4x4 parentMountWorld,
        out string? diagnostic)
        => TryResolveParentMountWorld(
            parentHierarchy,
            parentRigidWorld,
            parentScale,
            mount,
            animations,
            hierarchyOverrides: null,
            out parentMountWorld,
            out diagnostic);

    public static bool TryResolveParentMountWorld(
        EnbW3dRenderHierarchy parentHierarchy,
        Matrix4x4 parentRigidWorld,
        float parentScale,
        EnbClientBaseAssetMount mount,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample>? animations,
        IReadOnlyCollection<HierarchyPivotOverride>? hierarchyOverrides,
        out Matrix4x4 parentMountWorld,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(parentHierarchy);
        ArgumentNullException.ThrowIfNull(mount);
        parentMountWorld = default;
        diagnostic = null;

        if (!TryEvaluateScaledRigidHierarchy(
                parentHierarchy,
                parentRigidWorld,
                parentScale,
                animations,
                hierarchyOverrides,
                out Matrix4x4[] parentPose,
                out diagnostic))
        {
            return false;
        }

        if (mount.BoneIndex < 0 || mount.BoneIndex >= parentPose.Length)
        {
            diagnostic = $"BASE-{mount.BaseAssetId} mount '{mount.BoneName}' BoneIndex {mount.BoneIndex} is outside hierarchy '{parentHierarchy.Name}' pivot range 0..{parentPose.Length - 1}.";
            return false;
        }

        parentMountWorld = parentPose[mount.BoneIndex];
        return true;
    }

    /// <summary>
    /// Creates the exact hierarchy-record write performed by the ordinary Lego
    /// attachment helper: selected cbasset BoneIndex plus the child controller's
    /// stored ATTACH inverse. Applying this state to the parent and evaluating
    /// the selected record yields the child RenderObject's own transform.
    /// </summary>
    public static bool TryCreateParentHierarchyOverride(
        EnbClientBaseAssetMount mount,
        EnbW3dRenderHierarchy? childHierarchy,
        float childCreationScale,
        out HierarchyPivotOverride hierarchyOverride,
        out bool usedRetailMissingAttachFallback,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(mount);
        hierarchyOverride = default;
        if (mount.BoneIndex < 0)
        {
            usedRetailMissingAttachFallback = false;
            diagnostic = $"BASE-{mount.BaseAssetId} mount '{mount.BoneName}' has negative BoneIndex {mount.BoneIndex}.";
            return false;
        }
        if (!TryResolveChildAttachInverse(
                childHierarchy,
                childCreationScale,
                out Matrix4x4 childAttachInverse,
                out usedRetailMissingAttachFallback,
                out diagnostic))
        {
            return false;
        }

        hierarchyOverride = new HierarchyPivotOverride(mount.BoneIndex, childAttachInverse);
        return true;
    }

    internal sealed class AttachInverseOwner
    {
        public readonly object Gate=new();
        public AttachInversePublication? Published;
    }
    internal sealed record AttachInversePublication(uint Scale,bool Fallback,Matrix4x4 Inverse);

    public static bool TryResolveChildAttachInverse(EnbW3dRenderHierarchy? childHierarchy,
        float childCreationScale,out Matrix4x4 childAttachInverse,
        out bool usedRetailMissingNameFallback,out string? diagnostic)
    {
        // 72A7C0 captures the factory ATTACH inverse; animation and per-frame
        // parent placement never replace it. Configured hierarchy replacement
        // has a different weak owner, and a scale change replaces this value.
        if(childHierarchy is null)return TryResolveChildAttachInverseLiteral(childHierarchy,childCreationScale,
            out childAttachInverse,out usedRetailMissingNameFallback,out diagnostic);
        var owner=OwnerCache.Get(ref childHierarchy.DerivedAttachInverse,childHierarchy,static _=>new AttachInverseOwner());
        uint scale=BitConverter.SingleToUInt32Bits(childCreationScale);
        var state=Volatile.Read(ref owner.Published);
        if(state is not null && state.Scale==scale)
        {childAttachInverse=state.Inverse;usedRetailMissingNameFallback=state.Fallback;diagnostic=null;return true;}
        lock(owner.Gate)
        {
            state=Volatile.Read(ref owner.Published);
            if(state is not null && state.Scale==scale)
            {childAttachInverse=state.Inverse;usedRetailMissingNameFallback=state.Fallback;diagnostic=null;return true;}
            if(!TryResolveChildAttachInverseLiteral(childHierarchy,childCreationScale,
                out childAttachInverse,out usedRetailMissingNameFallback,out diagnostic))return false;
            Volatile.Write(ref owner.Published,new(scale,usedRetailMissingNameFallback,childAttachInverse));
            return true;
        }
    }

    internal static bool TryResolveChildAttachInverseLiteral(
        EnbW3dRenderHierarchy? childHierarchy,
        float childCreationScale,
        out Matrix4x4 childAttachInverse,
        out bool usedRetailMissingNameFallback,
        out string? diagnostic)
    {
        childAttachInverse = Matrix4x4.Identity;
        usedRetailMissingNameFallback = false;
        diagnostic = null;

        // A RenderObject with no hierarchy returns its root transform from the
        // same virtual path. With identity root placement that is identity, so
        // the attachment inverse is also identity.
        if (childHierarchy is null)
        {
            if (!float.IsFinite(childCreationScale))
            {
                diagnostic = $"Child Lego received non-finite creation scale {childCreationScale}.";
                return false;
            }
            return true;
        }

        if (!TryEvaluateScaledRigidHierarchy(
                childHierarchy,
                Matrix4x4.Identity,
                childCreationScale,
                out Matrix4x4[] childPose,
                out diagnostic))
        {
            return false;
        }
        if (!TryResolveRetailHierarchyPivot(
                childHierarchy,
                AttachTransformName,
                out int attachIndex,
                out usedRetailMissingNameFallback,
                out diagnostic))
        {
            return false;
        }
        if (attachIndex < 0 || attachIndex >= childPose.Length)
        {
            diagnostic = $"Hierarchy '{childHierarchy.Name}' resolved ATTACH pivot {attachIndex} outside its evaluated pose.";
            return false;
        }

        Matrix4x4 attach = childPose[attachIndex];
        //72A7C0 caches the named transform through8C2BC0/8C2BD0.
        //Its transposed basis and ordered translation products are observably
        //different from a determinant-based inverse even for rounded rotations.
        childAttachInverse=EnbRetailAffineRows.RigidInverse(attach);
        if (!IsFinite(childAttachInverse))
        {
            diagnostic = $"Hierarchy '{childHierarchy.Name}' ATTACH transform is not rigid-invertible.";
            childAttachInverse = Matrix4x4.Identity;
            return false;
        }

        return true;
    }

    /// <summary>
    /// The child retains its factory geometry scale (1.0) when attached.
    /// FUN_0098E4B0 copies LOD bias through v+12C; geometry scaling is v+164.
    /// </summary>
    public static Matrix4x4 CreateAttachedChildDrawWorld(
        Matrix4x4 rigidWorld)
    {
        if (!IsFinite(rigidWorld))
            throw new ArgumentException("Rigid world transform must be finite.", nameof(rigidWorld));
        return rigidWorld;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
