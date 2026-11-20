using System.Collections.Immutable;
using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>
/// Current-retail shared-budget semantics for the render-object family named by
/// the executable's own diagnostics/source strings as <c>SegLineRObj</c> and
/// <c>segline_create.cpp</c>. The family is also the base used by
/// ConstructSectorLoadLine.
///
/// Authored <c>SubDivs</c> determines the maximum selectable level. Current
/// retail clamps it to seven, uses the common 00941AF0 projected-detail metric,
/// and registers non-zero maximum-detail objects through 0096F0C0/0096F180.
/// A zero-SubDivs object contributes its current cost directly as fixed scene
/// cost instead of becoming a shared candidate.
/// </summary>
public static class EnbSegLineDetailBudgetSemantics
{
    public const int CurrentRetailMaximumLevelOrdinal = 7;
    public const int CurrentRetailInitialLevelOrdinal = 0;

    /// <summary>009696A0 endpoint box plus half width, unioned with segment
    /// midpoint boxes expanded by twice noise when maximum detail is nonzero;
    /// 00969640 converts that box to its circumscribed sphere.</summary>
    public static EnbW3dWorldBounds Bounds(IReadOnlyList<Vector3> points,float width,float noise,int maximumLevel)
    {
        if(points.Count<2 || !float.IsFinite(width) || width<=0 || !float.IsFinite(noise) || noise<0 || maximumLevel is <0 or >7)
            throw new ArgumentException("Invalid SegLine bounds inputs");
        var minimum=new Vector3(float.PositiveInfinity);var maximum=new Vector3(float.NegativeInfinity);
        foreach(var p in points)
        {
            if(!float.IsFinite(p.X)||!float.IsFinite(p.Y)||!float.IsFinite(p.Z))throw new ArgumentException("Nonfinite SegLine point");
            minimum=Vector3.Min(minimum,p);maximum=Vector3.Max(maximum,p);
        }
        minimum-=new Vector3(width*.5f);maximum+=new Vector3(width*.5f);
        if(maximumLevel>0)
            for(int i=1;i<points.Count;i++)
            {
                var midpoint=(points[i-1]+points[i])*.5f;
                var extent=new Vector3(width*.5f+2*noise);
                minimum=Vector3.Min(minimum,midpoint-extent);maximum=Vector3.Max(maximum,midpoint+extent);
            }
        return new((minimum+maximum)*.5f,((maximum-minimum)*.5f).Length());
    }

    /// <summary>
    /// 00969A90 obtains projected detail through inherited virtual +0x15C,
    /// which resolves to the same 00941AF0 metric already pinned.
    /// </summary>
    public static bool TryComputeProjectedDetail(
        EnbResolvedRenderCamera camera,
        EnbW3dWorldBounds bounds,
        out float projectedDetail)
        => EnbCurrentRetailProjectedDetail.TryCompute(camera, bounds, out projectedDetail);

    /// <summary>
    /// 00969380 treats its input as unsigned and clamps every value greater
    /// than six to seven. Thus the exact current range is 0..7.
    /// </summary>
    public static int ClampMaximumLevel(uint requestedSubDivisions)
        => requestedSubDivisions > 6u
            ? CurrentRetailMaximumLevelOrdinal
            : (int)requestedSubDivisions;

    /// <summary>
    /// 00969C00 first clamps a signed request below zero to zero, then clamps
    /// the result to this object's maximum level.
    /// </summary>
    public static int ClampCurrentLevel(int levelOrdinal, int maximumLevelOrdinal)
    {
        if ((uint)maximumLevelOrdinal > CurrentRetailMaximumLevelOrdinal)
            throw new ArgumentOutOfRangeException(nameof(maximumLevelOrdinal));
        return Math.Clamp(levelOrdinal, 0, maximumLevelOrdinal);
    }

    /// <summary>
    /// Reproduces the integer +0x20 render cost at 00969580 before +0x114
    /// converts it back to float with FILD:
    ///
    ///   2 * (pointCount - 1) * (1 &lt;&lt; level)
    ///
    /// The current config loader guarantees at least two points for authored
    /// Line objects. This keeps that valid-domain requirement explicit rather
    /// than reproducing the meaningless unsigned-underflow case.
    /// </summary>
    public static float CurrentRetailLevelCost(uint pointCount, int levelOrdinal)
    {
        if (pointCount < 2u)
            throw new ArgumentOutOfRangeException(nameof(pointCount));
        if ((uint)levelOrdinal > CurrentRetailMaximumLevelOrdinal)
            throw new ArgumentOutOfRangeException(nameof(levelOrdinal));

        int segmentCount = checked((int)pointCount - 1);
        int cost = unchecked((1 << levelOrdinal) * segmentCount);
        cost = unchecked(cost << 1);
        if (cost < 0)
            throw new ArgumentOutOfRangeException(nameof(pointCount), "Current-retail SegLine render cost overflowed the supported non-negative shared-budget domain.");
        return cost;
    }

    public static ImmutableArray<float> BuildLevelCosts(uint pointCount, int maximumLevelOrdinal)
    {
        if ((uint)maximumLevelOrdinal > CurrentRetailMaximumLevelOrdinal)
            throw new ArgumentOutOfRangeException(nameof(maximumLevelOrdinal));

        var costs = ImmutableArray.CreateBuilder<float>(maximumLevelOrdinal + 1);
        for (int level = 0; level <= maximumLevelOrdinal; level++)
            costs.Add(CurrentRetailLevelCost(pointCount, level));
        return costs.ToImmutable();
    }

    /// <summary>
    /// Builds the exact shared candidate represented by virtual slots
    /// +0x104..+0x128 at 00969A90..00969C40.
    ///
    /// For transition target level i &gt; 0 with target cost c:
    ///
    ///   priority[i] = (1 - 0.5/(c*c)) * projectedDetail / c
    ///
    /// Index zero is float.MaxValue and the final slot is -1.0f. Unlike HLOD,
    /// Sphere/Ring and ParticleBuffer, this family has no additional detail-
    /// scale multiplier in its priority virtuals.
    /// </summary>
    public static EnbRenderDetailOwner CreateCandidate(
        string name,
        uint pointCount,
        uint requestedSubDivisions,
        int currentLevelOrdinal,
        float projectedDetail)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Shared render-detail candidate name is required.", nameof(name));
        if (!float.IsFinite(projectedDetail) || projectedDetail < 0f)
            throw new ArgumentOutOfRangeException(nameof(projectedDetail));

        int maximumLevel = ClampMaximumLevel(requestedSubDivisions);
        if (maximumLevel == 0)
            throw new ArgumentException("Current retail accounts a zero-SubDivs SegLine object as fixed render cost rather than a shared-budget candidate.", nameof(requestedSubDivisions));

        ImmutableArray<float> costs = BuildLevelCosts(pointCount, maximumLevel);
        var priorities = ImmutableArray.CreateBuilder<float>(costs.Length + 1);
        priorities.Add(EnbW3dHlodBudgetCompiler.CurrentRetailNoDowngradeSentinel);

        for (int level = 1; level < costs.Length; level++)
        {
            float cost = costs[level];
            float quality = 1f - (0.5f / (cost * cost));
            priorities.Add(quality * projectedDetail / cost);
        }

        priorities.Add(EnbW3dHlodBudgetCompiler.CurrentRetailNoUpgradeSentinel);

        return new EnbRenderDetailOwner(
            name,
            ClampCurrentLevel(currentLevelOrdinal, maximumLevel),
            costs,
            priorities.ToImmutable());
    }

    /// <summary>
    /// Exact fixed contribution for the no-candidate path in 00969A90 when
    /// maximum detail/SubDivs is zero.
    /// </summary>
    public static float FixedRenderCost(uint pointCount)
        => CurrentRetailLevelCost(pointCount, CurrentRetailInitialLevelOrdinal);
}
