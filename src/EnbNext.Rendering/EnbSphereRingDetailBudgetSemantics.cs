using System.Collections.Immutable;

namespace EnbNext.Rendering;

/// <summary>
/// Current-retail shared-budget semantics for SpherePrototypeClass and
/// RingPrototypeClass. Both families use the common projected-detail metric and
/// the common 0096F0C0/0096F180 allocator, but own fixed family-specific level
/// cost tables and transition-priority preparation.
/// </summary>
public static class EnbSphereRingDetailBudgetSemantics
{
    public const int SphereInitialLevelOrdinal = 10;
    public const int RingInitialLevelOrdinal = 20;

    /// <summary>
    /// Actual SpherePrototypeClass costs exposed through virtual +0x114.
    /// Retail seeds the raw level-zero float table with 1e-6f, but +0x20 sends
    /// that value through __ftol and +0x114 converts the resulting integer back
    /// to float. The shared allocator therefore sees literal zero at level 0.
    /// </summary>
    public static ImmutableArray<float> SphereLevelCosts { get; } =
        ImmutableArray.Create(
            0f,
            98f,
            128f,
            162f,
            200f,
            242f,
            288f,
            338f,
            392f,
            450f,
            512f);

    /// <summary>
    /// Actual RingPrototypeClass costs exposed through virtual +0x114. As with
    /// spheres, the raw 1e-6f level-zero table seed becomes integer zero at the
    /// current-cost virtual boundary.
    /// </summary>
    public static ImmutableArray<float> RingLevelCosts { get; } =
        ImmutableArray.Create(
            0f,
            20f,
            24f,
            28f,
            32f,
            36f,
            40f,
            44f,
            48f,
            52f,
            56f,
            60f,
            64f,
            68f,
            72f,
            76f,
            80f,
            84f,
            88f,
            92f,
            96f);

    public static int ClampSphereLevel(int levelOrdinal)
        => Math.Clamp(levelOrdinal, 0, SphereInitialLevelOrdinal);

    public static int ClampRingLevel(int levelOrdinal)
        => Math.Clamp(levelOrdinal, 0, RingInitialLevelOrdinal);

    public static EnbRenderDetailOwner CreateSphereCandidate(
        string name,
        int currentLevelOrdinal,
        float projectedDetail,
        float detailScale = 1f)
        => CreateCandidate(
            name,
            ClampSphereLevel(currentLevelOrdinal),
            SphereLevelCosts,
            projectedDetail,
            detailScale);

    public static EnbRenderDetailOwner CreateRingCandidate(
        string name,
        int currentLevelOrdinal,
        float projectedDetail,
        float detailScale = 1f)
        => CreateCandidate(
            name,
            ClampRingLevel(currentLevelOrdinal),
            RingLevelCosts,
            projectedDetail,
            detailScale);

    private static EnbRenderDetailOwner CreateCandidate(
        string name,
        int currentLevelOrdinal,
        ImmutableArray<float> levelCosts,
        float projectedDetail,
        float detailScale)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Shared render-detail candidate name is required.", nameof(name));
        if (!float.IsFinite(projectedDetail) || projectedDetail < 0f)
            throw new ArgumentOutOfRangeException(nameof(projectedDetail));
        if (!float.IsFinite(detailScale))
            throw new ArgumentOutOfRangeException(nameof(detailScale));

        var priorities = ImmutableArray.CreateBuilder<float>(levelCosts.Length + 1);
        priorities.Add(EnbW3dHlodBudgetCompiler.CurrentRetailNoDowngradeSentinel);

        // 0090D180 / 009122C0 iterate only the positive raw cost entries.
        // For each cost c the x87 sequence is:
        //   (1 - 0.5 / (c*c)) * detailScale * projectedDetail / c
        // before the common no-upgrade sentinel is appended.
        for (int index = 1; index < levelCosts.Length; index++)
        {
            float cost = levelCosts[index];
            float squaredCost = cost * cost;
            float quality = 1f - (0.5f / squaredCost);
            priorities.Add(quality * detailScale * projectedDetail / cost);
        }

        priorities.Add(EnbW3dHlodBudgetCompiler.CurrentRetailNoUpgradeSentinel);

        return new EnbRenderDetailOwner(
            name,
            currentLevelOrdinal,
            levelCosts,
            priorities.ToImmutable());
    }
}
