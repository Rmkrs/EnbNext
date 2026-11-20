using System.Collections.Immutable;

namespace EnbNext.Rendering;

/// <summary>
/// One root RenderObjClass that uses current retail's inherited fixed-cost
/// detail-preparation path at 00942020. The caller provides the object's
/// already-resolved +0x114 current cost because family-specific +0x20 cost
/// ownership remains with the concrete render-object implementation.
/// </summary>
public sealed record EnbFixedRenderCostSceneInstance(
    string Name,
    EnbCurrentRetailRenderObjectState RuntimeState,
    EnbW3dWorldBounds? Bounds,
    float CurrentCost);

public sealed record EnbFixedRenderCostSceneEntry(
    string Name,
    bool? FrustumVisible,
    bool PreparedForDetail,
    float CurrentCost,
    float ContributedCost);

public sealed record EnbFixedRenderCostSceneGatherResult(
    float FixedRenderCost,
    ImmutableArray<EnbFixedRenderCostSceneEntry> Entries);

/// <summary>
/// Current-retail inherited fixed-cost scene accounting.
///
/// 00965F40 performs the common root scene visibility/activity gate and then
/// calls RenderObjClass virtual +0x104. Across the current executable there are
/// 39 vtables whose +0x104 slot resolves to 00942020. That inherited function
/// adds virtual +0x114 directly to the process-global current render cost.
///
/// 38 of those 39 vtables inherit +0x114 = 00942040. The default +0x114 bridge
/// calls integer render cost virtual +0x20, converts a non-zero signed integer
/// to float, and substitutes 1e-6f only when the integer result is exactly zero.
/// The remaining vtable is the star manager, whose custom +0x114 returns its
/// StarBudget-capped float cost.
///
/// This boundary is intentionally polymorphic. It does not hardcode a class
/// whitelist or attempt to reinterpret composite/delegating +0x20 methods.
/// Concrete render-object families own their current cost; this gatherer owns
/// the common scene-admission and fixed-aggregate behavior.
/// </summary>
public static class EnbCurrentRetailFixedRenderCost
{
    public const float DefaultZeroIntegerCostFallback = 1e-6f;

    /// <summary>
    /// Single-precision stored result of the default00942040 bridge. The
    /// register result retains a signed integer until a later store; the
    /// scene-gather boundary separately rejects negative current costs as an
    /// invalid EnbNext runtime state.
    /// </summary>
    public static float FromDefaultIntegerCost(int currentIntegerRenderCost)
        => currentIntegerRenderCost == 0
            ? DefaultZeroIntegerCostFallback
            : (float)currentIntegerRenderCost;

    /// <summary>Whole00942020 addition composed with00942040. Retains the
    /// signed integer until native PC24 addition and the global float store.</summary>
    public static float AddDefaultIntegerCost(float currentSum,int currentIntegerRenderCost,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
        =>EnbCurrentRetailRenderObjectQueryStorage.AddFloatingCost(currentSum,currentIntegerRenderCost==0?(double)DefaultZeroIntegerCostFallback:currentIntegerRenderCost,precision);
}
