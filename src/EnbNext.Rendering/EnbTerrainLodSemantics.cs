namespace EnbNext.Rendering;

/// <summary>
/// Current-retail planet-terrain LOD policy driven by the effective LandLOD
/// graphics setting. This is deliberately separate from the shared projected-
/// detail allocator: retail selects terrain patch detail here first, then lets
/// LandPolyBudget constrain the later shared candidate pass.
/// </summary>
public static class EnbTerrainLodSemantics
{
    private static readonly int[] RetailBaseDistanceThresholds =
    [
        8,
        16,
        24,
        32,
        48,
        64,
        96,
        128,
    ];

    public static int GetBaseDistanceThreshold(int landLod)
        => RetailBaseDistanceThresholds[Math.Clamp(landLod, 0, 7)];

    /// <summary>
    /// Selects the current-retail terrain patch LOD from an already-computed
    /// non-negative Euclidean camera-to-patch distance. Level 0 is the finest
    /// patch representation and level 4 is the coarsest.
    ///
    /// Retail converts the positive distance through __ftol before comparing it
    /// against the LandLOD threshold table, so the fractional component is
    /// discarded before every boundary test. Equality advances to the next
    /// coarser level because the executable uses a strict distance &lt; threshold
    /// comparison.
    /// </summary>
    public static int SelectPatchLevel(float cameraDistance, int landLod)
    {
        if (!float.IsFinite(cameraDistance) || cameraDistance < 0f || cameraDistance > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(cameraDistance));

        int distance = (int)cameraDistance;
        int threshold = GetBaseDistanceThreshold(landLod);

        for (int level = 0; level < 4; level++)
        {
            if (distance < threshold)
                return level;

            threshold <<= 1;
        }

        return 4;
    }

    /// <summary>
    /// Reproduces the four-bit neighbor relation written by the retail terrain
    /// updater after patch levels are selected. A bit is set only when the
    /// neighbor on that edge has a numerically lower (finer) LOD than the
    /// current patch.
    /// </summary>
    public static EnbTerrainLodNeighborMask BuildNeighborMask(
        int currentLevel,
        int negativeU,
        int positiveU,
        int negativeV,
        int positiveV)
    {
        ValidatePatchLevel(currentLevel, nameof(currentLevel));
        ValidatePatchLevel(negativeU, nameof(negativeU));
        ValidatePatchLevel(positiveU, nameof(positiveU));
        ValidatePatchLevel(negativeV, nameof(negativeV));
        ValidatePatchLevel(positiveV, nameof(positiveV));

        EnbTerrainLodNeighborMask result = EnbTerrainLodNeighborMask.None;
        if (negativeU < currentLevel)
            result |= EnbTerrainLodNeighborMask.NegativeU;
        if (positiveU < currentLevel)
            result |= EnbTerrainLodNeighborMask.PositiveU;
        if (negativeV < currentLevel)
            result |= EnbTerrainLodNeighborMask.NegativeV;
        if (positiveV < currentLevel)
            result |= EnbTerrainLodNeighborMask.PositiveV;
        return result;
    }

    private static void ValidatePatchLevel(int value, string paramName)
    {
        if ((uint)value > 4u)
            throw new ArgumentOutOfRangeException(paramName);
    }
}

[Flags]
public enum EnbTerrainLodNeighborMask : byte
{
    None = 0,
    NegativeU = 1 << 0,
    PositiveU = 1 << 1,
    NegativeV = 1 << 2,
    PositiveV = 1 << 3,
}
