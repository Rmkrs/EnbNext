using System.Collections.Immutable;
using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>
/// One ParticleBufferClass frame's custom projected-detail state. Current retail
/// does not feed the common 00941AF0 sphere metric straight into the shared
/// allocator for this family: 0095EAD0 bounds a sphere term by a particle-count
/// term and exponentially smooths the result before rebuilding transition state.
/// </summary>
public readonly record struct EnbParticleBufferProjectedDetail(
    float Sample,
    float Smoothed);

/// <summary>
/// Current-retail ParticleBufferClass semantics at the polymorphic
/// 0096F0C0/0096F180 shared render-detail boundary.
///
/// The names CountLikeValue and PerElementRadius intentionally stay neutral.
/// Their exact higher-level business labels are not required by the budget
/// contract; the current binary proves how the values participate in cost and
/// projected-detail calculations.
/// </summary>
public static class EnbParticleBufferDetailBudgetSemantics
{
    public const int CurrentRetailPrimaryLevelCount = 17;
    public const int CurrentRetailPrimaryInitialLevelOrdinal = 16;

    private static readonly int[] PhysicalSlotDetailOrder = [11, 3, 7, 14, 0, 13, 1, 2, 5, 12, 15, 6, 9, 8, 4, 10];

    /// <summary>0095DA60/0095DFD0: select the physical circular-buffer slot before compacting draw streams.</summary>
    public static bool IncludesPhysicalSlot(int physicalSlot, int internalDetailLevel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(physicalSlot);
        ArgumentOutOfRangeException.ThrowIfNegative(internalDetailLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(internalDetailLevel, 16);
        return PhysicalSlotDetailOrder[physicalSlot & 15] >= internalDetailLevel;
    }

    public const float CurrentRetailZeroCostFallback = 1e-6f;
    public const float CurrentRetailQualityCostThreshold = 0.0001f;
    public const float CurrentRetailPreviousSampleWeight = 0.9f;
    public const float CurrentRetailNewSampleWeight = 0.1f;

    /// <summary>
    /// The copy constructor at 0095C360 caps +0x21C at 17, otherwise retaining
    /// the object's unsigned +0xC4 count-like value. The primary constructor
    /// uses a fixed 17 levels.
    /// </summary>
    public static int ResolveCopiedLevelCount(uint countLikeValue)
        => (int)Math.Min(countLikeValue, (uint)CurrentRetailPrimaryLevelCount);

    /// <summary>
    /// Reproduces 0095ED60's five numeric cost modes. Unknown modes retain the
    /// function's initial zero step, which makes every generated level cost use
    /// the 1e-6f zero-cost fallback.
    /// </summary>
    public static ImmutableArray<float> BuildLevelCosts(
        uint countLikeValue,
        int costMode,
        int levelCount = CurrentRetailPrimaryLevelCount)
    {
        ValidateLevelCount(levelCount);

        float step = costMode switch
        {
            0 => countLikeValue * 0.0625f,
            1 => countLikeValue * 0.125f,
            2 => unchecked(countLikeValue * 2u - 1u) * 0.0625f,
            3 => countLikeValue * 0.25f,
            4 => countLikeValue * 0.5f,
            _ => 0f,
        };

        var costs = ImmutableArray.CreateBuilder<float>(levelCount);
        for (int level = 0; level < levelCount; level++)
        {
            float cost = level * step;
            costs.Add(cost == 0f ? CurrentRetailZeroCostFallback : cost);
        }

        return costs.ToImmutable();
    }

    /// <summary>
    /// Reproduces the custom metric prepared by ParticleBufferClass::+0x104 at
    /// 0095EAD0. Bounds must be the particle buffer's current expanded bounding
    /// sphere returned by its +0xF0 virtual. PerElementRadius is the current
    /// +0xC0 float used by the second bounded-area term.
    /// </summary>
    public static bool TryComputeProjectedDetail(
        EnbResolvedRenderCamera camera,
        EnbW3dWorldBounds bounds,
        uint countLikeValue,
        float perElementRadius,
        float previousSmoothedProjectedDetail,
        out EnbParticleBufferProjectedDetail projectedDetail)
    {
        projectedDetail = default;
        if (!bounds.IsValid ||
            !float.IsFinite(perElementRadius) ||
            !float.IsFinite(previousSmoothedProjectedDetail) || previousSmoothedProjectedDetail < 0f)
        {
            return false;
        }

        Vector3 relative = bounds.Center - camera.Position;
        float distance = relative.Length();
        if (!float.IsFinite(distance))
            return false;

        float boundedAreaTerm = 0f;
        if (camera.Projection.IsOrthographic)
        {
            float sphereAreaTerm = bounds.Radius * bounds.Radius;
            float particleAreaTerm = countLikeValue * perElementRadius * perElementRadius;
            boundedAreaTerm = MathF.Min(sphereAreaTerm, particleAreaTerm);
        }
        else if (distance != 0f)
        {
            float inverseDistance = 1f / distance;
            float sphereRadiusOverDistance = bounds.Radius * inverseDistance;
            float perElementRadiusOverDistance = perElementRadius * inverseDistance;

            float sphereAreaTerm = sphereRadiusOverDistance * sphereRadiusOverDistance;
            float particleAreaTerm =
                countLikeValue * perElementRadiusOverDistance * perElementRadiusOverDistance;
            boundedAreaTerm = MathF.Min(sphereAreaTerm, particleAreaTerm);
        }

        float horizontalPixelsPerProjectionUnit =
            camera.Viewport.PixelWidth * 0.5f * camera.Projection.Matrix.M11;
        float verticalPixelsPerProjectionUnit =
            camera.Viewport.PixelHeight * 0.5f * camera.Projection.Matrix.M22;

        float sample =
            boundedAreaTerm *
            verticalPixelsPerProjectionUnit *
            horizontalPixelsPerProjectionUnit *
            MathF.PI;

        float smoothed =
            previousSmoothedProjectedDetail * CurrentRetailPreviousSampleWeight +
            sample * CurrentRetailNewSampleWeight;

        if (!float.IsFinite(sample) || sample < 0f ||
            !float.IsFinite(smoothed) || smoothed < 0f)
        {
            return false;
        }

        projectedDetail = new EnbParticleBufferProjectedDetail(sample, smoothed);
        return true;
    }

    /// <summary>
    /// Builds the shared allocator candidate prepared by 0095ED60. The current
    /// executable's 17-entry threshold table at 00BBB148 is entirely
    /// float.MaxValue, so every finite projected-detail value has minimum/floor
    /// ordinal zero. Transition index zero is therefore the no-downgrade
    /// sentinel and positive levels use the guarded current-retail formula.
    /// </summary>
    public static EnbRenderDetailOwner CreateCandidate(
        string name,
        int currentLevelOrdinal,
        uint countLikeValue,
        int costMode,
        float smoothedProjectedDetail,
        float detailScale = 1f,
        int levelCount = CurrentRetailPrimaryLevelCount)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Shared render-detail candidate name is required.", nameof(name));
        if (!float.IsFinite(smoothedProjectedDetail) || smoothedProjectedDetail < 0f)
            throw new ArgumentOutOfRangeException(nameof(smoothedProjectedDetail));
        if (!float.IsFinite(detailScale))
            throw new ArgumentOutOfRangeException(nameof(detailScale));
        ValidateLevelCount(levelCount);

        ImmutableArray<float> levelCosts = BuildLevelCosts(countLikeValue, costMode, levelCount);
        var priorities = ImmutableArray.CreateBuilder<float>(levelCount + 1);
        priorities.Add(EnbW3dHlodBudgetCompiler.CurrentRetailNoDowngradeSentinel);

        for (int level = 1; level < levelCount; level++)
        {
            float cost = levelCosts[level];
            float quality = 0f;
            if (cost > CurrentRetailQualityCostThreshold)
                quality = 1f - (0.5f / (cost * cost));

            priorities.Add(quality * detailScale * smoothedProjectedDetail / cost);
        }

        priorities.Add(EnbW3dHlodBudgetCompiler.CurrentRetailNoUpgradeSentinel);

        return new EnbRenderDetailOwner(
            name,
            Math.Clamp(currentLevelOrdinal, 0, levelCount - 1),
            levelCosts,
            priorities.ToImmutable());
    }

    /// <summary>0095ED60 tables belong to a physical buffer. Its capacity and
    /// mode are construction inputs; only priorities and level change per view.</summary>
    public sealed class Owner
    {
        private readonly float[] priorities;
        public EnbRenderDetailOwner Detail {get;}
        public Owner(string name,uint countLikeValue,int costMode,int levelCount=CurrentRetailPrimaryLevelCount)
        {
            if(string.IsNullOrWhiteSpace(name))throw new ArgumentException("Shared render-detail candidate name is required.",nameof(name));
            var costs=BuildLevelCosts(countLikeValue,costMode,levelCount);
            priorities=new float[levelCount+1];
            priorities[0]=EnbW3dHlodBudgetCompiler.CurrentRetailNoDowngradeSentinel;
            priorities[levelCount]=EnbW3dHlodBudgetCompiler.CurrentRetailNoUpgradeSentinel;
            Detail=new(name,0,costs,priorities);
        }
        public EnbRenderDetailOwner Update(int currentLevelOrdinal,float smoothedProjectedDetail,float detailScale=1f)
        {
            if(!float.IsFinite(smoothedProjectedDetail)||smoothedProjectedDetail<0f)throw new ArgumentOutOfRangeException(nameof(smoothedProjectedDetail));
            if(!float.IsFinite(detailScale))throw new ArgumentOutOfRangeException(nameof(detailScale));
            for(int level=1;level<Detail.LevelCount;level++)
            {
                float cost=Detail.LevelCosts[level];float quality=0f;
                if(cost>CurrentRetailQualityCostThreshold)quality=1f-(0.5f/(cost*cost));
                priorities[level]=quality*detailScale*smoothedProjectedDetail/cost;
            }
            Detail.CurrentLevelOrdinal=Math.Clamp(currentLevelOrdinal,0,Detail.LevelCount-1);
            return Detail;
        }
    }

    private static void ValidateLevelCount(int levelCount)
    {
        if (levelCount < 1 || levelCount > CurrentRetailPrimaryLevelCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(levelCount),
                $"ParticleBufferClass shared-detail level count must be between 1 and {CurrentRetailPrimaryLevelCount}.");
        }
    }
}
