using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>
/// Current-retail fixed ship visual children reconstructed from
/// InitializeShipVisualEffects @ 0x006FCB70 and its recursive attachment helper
/// @ 0x006F3810 in the current retail client.
///
/// Eligible ordinary ships create six scale-1 BaseAsset RenderObjects. The
/// recursive helper searches the parent RenderObject tree for the named pivot
/// and attaches only when the hierarchy resolver returns a non-zero index.
/// Unlike Lego placement, no child ATTACH inverse is applied.
/// </summary>
public static class EnbCurrentRetailShipFixedVisualRuntime
{
    public const int LeftPrimaryBaseAsset = 0x0618;
    public const int RightPrimaryBaseAsset = 0x0617;
    public const int LeftRightSecondaryBaseAsset = 0x073F;

    public static ImmutableArray<EnbCurrentRetailShipFixedVisualSpec> Specs { get; } =
        ImmutableArray.Create(
            new EnbCurrentRetailShipFixedVisualSpec(0, LeftPrimaryBaseAsset, "~FL"),
            new EnbCurrentRetailShipFixedVisualSpec(1, RightPrimaryBaseAsset, "~FR"),
            new EnbCurrentRetailShipFixedVisualSpec(2, LeftPrimaryBaseAsset, "~BL"),
            new EnbCurrentRetailShipFixedVisualSpec(3, RightPrimaryBaseAsset, "~BR"),
            new EnbCurrentRetailShipFixedVisualSpec(4, LeftRightSecondaryBaseAsset, "~FL"),
            new EnbCurrentRetailShipFixedVisualSpec(5, LeftRightSecondaryBaseAsset, "~FR"));

    //006FCB70 creates +158,+160,+15C,+164,+168,+16C. The later
    //006F3810 attachment walk instead follows the contiguous slot order.
    public static ImmutableArray<EnbCurrentRetailShipFixedVisualSpec> FactorySpecs {get;} =
        new[]{0,2,1,3,4,5}.Select(slot=>Specs[slot]).ToImmutableArray();

    public static bool IsEligible(IEnbRenderObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return obj.ObjectType is 0 or 1 && obj.ShipPresentation?.IsOrganic != true;
    }

    public static bool TryResolveBoneWorld(
        EnbW3dRenderHierarchy hierarchy,
        Matrix4x4 parentRigidWorld,
        float parentScale,
        string boneName,
        out Matrix4x4 boneWorld,
        out bool found,
        out string? diagnostic)
        => TryResolveBoneWorld(
            hierarchy,
            parentRigidWorld,
            parentScale,
            boneName,
            animations: null,
            hierarchyOverrides: null,
            out boneWorld,
            out found,
            out diagnostic);

    public static bool TryResolveBoneWorld(
        EnbW3dRenderHierarchy hierarchy,
        Matrix4x4 parentRigidWorld,
        float parentScale,
        string boneName,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample>? animations,
        out Matrix4x4 boneWorld,
        out bool found,
        out string? diagnostic)
        => TryResolveBoneWorld(
            hierarchy,
            parentRigidWorld,
            parentScale,
            boneName,
            animations,
            hierarchyOverrides: null,
            out boneWorld,
            out found,
            out diagnostic);

    public static bool TryResolveBoneWorld(
        EnbW3dRenderHierarchy hierarchy,
        Matrix4x4 parentRigidWorld,
        float parentScale,
        string boneName,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample>? animations,
        IReadOnlyCollection<EnbCurrentRetailShipLegoRuntime.HierarchyPivotOverride>? hierarchyOverrides,
        out Matrix4x4 boneWorld,
        out bool found,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        ArgumentException.ThrowIfNullOrWhiteSpace(boneName);
        boneWorld = default;
        found = false;
        diagnostic = null;

        if (!EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchy(
                hierarchy,
                parentRigidWorld,
                parentScale,
                animations,
                hierarchyOverrides,
                out Matrix4x4[] pose,
                out diagnostic))
        {
            return false;
        }

        return TryResolveBoneWorld(hierarchy,pose,boneName,out boneWorld,out found,out diagnostic);
    }

    /// <summary>Queries another fixed child from the same evaluated parent pose.
    /// The caller owns the pivot fields and their update/invalidation boundary.</summary>
    public static bool TryResolveBoneWorld(EnbW3dRenderHierarchy hierarchy,
        ReadOnlySpan<Matrix4x4> pose,string boneName,out Matrix4x4 boneWorld,
        out bool found,out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        ArgumentException.ThrowIfNullOrWhiteSpace(boneName);
        boneWorld=default;found=false;diagnostic=null;
        if (!EnbCurrentRetailShipLegoRuntime.TryResolveRetailHierarchyPivot(
                hierarchy,
                boneName,
                out int pivotIndex,
                out bool usedMissingNameFallback,
                out diagnostic))
        {
            return false;
        }

        // RenderObject+0xB0 returns hierarchy index 0 when the name is absent;
        // InitializeShipVisualEffects explicitly tests the result for non-zero
        // before calling the child-attachment virtual.
        if (usedMissingNameFallback || pivotIndex == 0)
            return true;
        if (pivotIndex < 0 || pivotIndex >= pose.Length)
        {
            diagnostic = $"Hierarchy '{hierarchy.Name}' fixed-visual bone '{boneName}' resolved pivot {pivotIndex} outside its evaluated pose.";
            return false;
        }

        boneWorld = pose[pivotIndex];
        found = true;
        return true;
    }
}

public sealed record EnbCurrentRetailShipFixedVisualSpec(
    int Slot,
    int BaseAsset,
    string BoneName);
