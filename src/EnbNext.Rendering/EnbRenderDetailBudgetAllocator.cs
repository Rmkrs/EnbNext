using System.Collections.Immutable;
using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>
/// Current-retail projected-detail metric used by the shared render-budget
/// allocator. FUN_00941AF0 uses Euclidean camera-to-sphere-center distance and
/// stored viewport/projection scale, without converting to physical pixels.
/// </summary>
public static class EnbCurrentRetailProjectedDetail
{
    public static bool TryCompute(
        EnbResolvedRenderCamera camera,
        EnbW3dWorldBounds bounds,
        out float projectedDetail)
    {
        projectedDetail = 0f;
        if(camera.Projection.StoredProjectionBounds is not {} projectionBounds)return false;
        projectedDetail=EnbCurrentRetailProjectedDetailStorage.Compute(camera.Position,
            camera.CurrentRetailDetailViewport,projectionBounds,new Vector4(bounds.Center,bounds.Radius));
        return true;
    }
}

/// <summary>
/// Mutable owner-held HLOD detail state. TransitionPriorities follows
/// current retail's HLOD array convention:
///
///   index i     = priority for the transition from level i-1 to level i
///   index count = -1.0f sentinel meaning no higher-detail transition
///
/// Entries from zero through MinimumLevelOrdinal are float.MaxValue, preventing
/// the shared allocator from reducing detail below the authored threshold floor.
/// </summary>
public sealed class EnbHlodDetailOwner(
    string Name, int CurrentLevelOrdinal, int MinimumLevelOrdinal,
    IReadOnlyList<float> LevelCosts, IReadOnlyList<float> TransitionPriorities,
    float ProjectedDetail)
{
    internal EnbRenderDetailOwner Shared {get;}=new(Name,CurrentLevelOrdinal,LevelCosts,TransitionPriorities);
    public string Name=>Shared.Name;
    public int CurrentLevelOrdinal {get=>Shared.CurrentLevelOrdinal;internal set=>Shared.CurrentLevelOrdinal=value;}
    public int MinimumLevelOrdinal {get;internal set;}=MinimumLevelOrdinal;
    public float ProjectedDetail {get;internal set;}=ProjectedDetail;
    public IReadOnlyList<float> LevelCosts=>Shared.LevelCosts;
    public IReadOnlyList<float> TransitionPriorities=>Shared.TransitionPriorities;
    public int LevelCount => LevelCosts.Count;
    public float CurrentCost => Shared.CurrentCost;
    public float DowngradePriority => Shared.DowngradePriority;
    public float UpgradePriority => Shared.UpgradePriority;
    public EnbW3dHlodSelection Selection => new(Name, CurrentLevelOrdinal);
}

/// <summary>
/// Builds current-retail HLOD candidate state from the authored thresholds and
/// the current frame's render cost for each level. The caller owns current
/// member activity and aggregate HLOD bounds; those are scene-runtime facts and
/// are intentionally not guessed from immutable W3D data.
/// </summary>
public static class EnbW3dHlodBudgetCompiler
{
    public const float CurrentRetailZeroCostFallback = 1e-6f;
    public const float CurrentRetailNoUpgradeSentinel = -1f;
    public const float CurrentRetailNoDowngradeSentinel = float.MaxValue;

    /// <summary>0098C420/0098CA20 initialize +E8 to zero, then promote it
    /// using0098EE30 at detail1. A clone repeats this initialization rather
    /// than inheriting the source object's currently selected level.</summary>
    public static int CurrentRetailInitialLevel(EnbW3dRenderHlod hlod)
    {
        ArgumentNullException.ThrowIfNull(hlod);
        int level = 0;
        while (level + 1 < hlod.Levels.Count && (hlod.Levels[level].Threshold < 1f || float.IsNaN(hlod.Levels[level].Threshold)))
            level++;
        return level;
    }

    public static bool TryCreateCandidate(
        EnbW3dRenderHlod hlod,
        int currentLevelOrdinal,
        EnbResolvedRenderCamera camera,
        EnbW3dWorldBounds bounds,
        IReadOnlyList<int> activeLevelRenderCosts,
        float lodScale,
        out EnbHlodDetailOwner candidate,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(hlod);
        ArgumentNullException.ThrowIfNull(activeLevelRenderCosts);
        candidate = default!;
        diagnostic = null;

        if (hlod.Levels.Count == 0)
        {
            diagnostic = $"W3D HLOD '{hlod.Name}' contains no levels.";
            return false;
        }

        // FUN_0098E9A0 does not register one-level HLODs with the shared
        // 0096F180 allocator. It adds their current +0x114 cost directly to
        // the shared aggregate instead. Keep that ownership explicit so an
        // EnbNext runtime cannot accidentally turn a fixed contribution into
        // a selectable candidate.
        if (hlod.Levels.Count == 1)
        {
            diagnostic = $"W3D HLOD '{hlod.Name}' has one level; current retail accounts it as fixed render cost instead of a shared-budget candidate.";
            return false;
        }

        if (activeLevelRenderCosts.Count != hlod.Levels.Count)
        {
            diagnostic = $"W3D HLOD '{hlod.Name}' has {hlod.Levels.Count} levels but {activeLevelRenderCosts.Count} current render-cost entries.";
            return false;
        }

        if (!float.IsFinite(lodScale))
        {
            diagnostic = $"W3D HLOD '{hlod.Name}' received a non-finite LOD scale.";
            return false;
        }
        for (int index = 0; index < hlod.Levels.Count; index++)
        {
            if (hlod.Levels[index].Ordinal != index)
            {
                diagnostic = $"W3D HLOD '{hlod.Name}' level order is not the contiguous current-retail ordinal sequence at index {index}.";
                return false;
            }
            if (activeLevelRenderCosts[index] < 0)
            {
                diagnostic = $"W3D HLOD '{hlod.Name}' level {index} has negative current render cost {activeLevelRenderCosts[index]}.";
                return false;
            }
        }

        if (!EnbCurrentRetailProjectedDetail.TryCompute(camera, bounds, out float projectedDetail))
        {
            diagnostic = $"W3D HLOD '{hlod.Name}' produced an invalid current-retail projected-detail metric.";
            return false;
        }

        int count = hlod.Levels.Count;
        var levels = new EnbCurrentRetailHlodDetailRuntime.Level[count];
        for (int index = 0; index < count; index++)
            levels[index] = EnbCurrentRetailHlodDetailRuntime.FromCost(hlod.Levels[index].Threshold, activeLevelRenderCosts[index]);
        var tables = EnbCurrentRetailHlodDetailRuntime.CreateTables(levels, projectedDetail, lodScale);
        int minimumLevel = tables.MinimumLevel;

        int currentLevel = Math.Clamp(currentLevelOrdinal, 0, count - 1);
        if (currentLevel < minimumLevel)
            currentLevel = minimumLevel;

        candidate = new EnbHlodDetailOwner(
            hlod.Name,
            currentLevel,
            minimumLevel,
            tables.Costs.ToImmutableArray(),
            tables.Priorities.ToImmutableArray(),
            projectedDetail);
        return true;
    }

    /// <summary>
    /// Converts the signed integer render-object contribution used by
    /// FUN_0098EA70 into its per-level float cost. A zero sum is represented by
    /// the current executable's 1e-6f fallback rather than literal zero. This
    /// helper is also the fixed-cost boundary for one-level HLODs, which current
    /// retail does not register with the shared allocator.
    /// </summary>
    public static float CurrentRetailLevelCost(int renderCost)
    {
        if (renderCost < 0)
            throw new ArgumentOutOfRangeException(nameof(renderCost));
        return renderCost == 0 ? CurrentRetailZeroCostFallback : (float)renderCost;
    }

    /// <summary>
    /// Resolves the current-retail integer +0x20 cost for each HLOD level from
    /// immutable W3D render objects, while leaving the per-member +0x174 active
    /// predicate explicit. Current MeshClass +0x20 is exactly:
    ///
    ///   triangleCount * activeMaterialDescriptorPassCount
    ///
    /// The render-resource compiler preserves that pass count separately from
    /// the higher-level material-pass list so the descriptor's default-one
    /// behavior is not lost when MaterialInfo is absent.
    /// </summary>
    public static bool TryComputeW3dLevelRenderCosts(
        EnbW3dRenderResource resource,
        EnbW3dRenderHlod hlod,
        Func<EnbW3dRenderHlodMember, bool> isMemberActive,
        out ImmutableArray<int> levelRenderCosts,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(hlod);
        ArgumentNullException.ThrowIfNull(isMemberActive);
        levelRenderCosts = ImmutableArray<int>.Empty;
        diagnostic = null;

        var costs = ImmutableArray.CreateBuilder<int>(hlod.Levels.Count);
        foreach (EnbW3dRenderHlodLevel level in hlod.Levels)
        {
            int total = 0;
            foreach (EnbW3dRenderHlodMember member in level.Members)
            {
                if (!isMemberActive(member))
                    continue;

                if (!TryResolveNamedRenderObjectCost(resource, member.Name, out int memberCost, out diagnostic))
                    return false;

                total = unchecked(total + memberCost);
            }
            costs.Add(total);
        }

        levelRenderCosts = costs.ToImmutable();
        return true;
    }

    /// <summary>
    /// Computes the fixed current-frame contribution of the materialized HLOD
    /// 0x0705 aggregate. FUN_0098E9A0 walks the secondary child array at
    /// +0x100/+0x10C after preparing the HLOD itself and calls each active
    /// child's +0x104 virtual. For MeshClass, +0x104 / 00942020 adds the
    /// child's +0x114 float cost directly to the shared fixed aggregate, so the
    /// zero-cost 1e-6 fallback applies per aggregate child rather than once to
    /// the sum.
    /// </summary>
    public static bool TryComputeW3dAggregateFixedRenderCost(
        EnbW3dRenderResource resource,
        EnbW3dRenderHlod hlod,
        Func<EnbW3dRenderHlodMember, bool> isMemberActive,
        out float fixedRenderCost,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(hlod);
        ArgumentNullException.ThrowIfNull(isMemberActive);
        fixedRenderCost = 0f;
        diagnostic = null;

        if (hlod.Aggregate is not EnbW3dRenderHlodAggregate aggregate)
            return true;

        foreach (EnbW3dRenderHlodMember member in aggregate.Members)
        {
            if (!isMemberActive(member))
                continue;

            if (!TryResolveNamedRenderObjectCost(resource, member.Name, out int memberCost, out diagnostic))
                return false;

            fixedRenderCost += CurrentRetailLevelCost(memberCost);
            if (!float.IsFinite(fixedRenderCost))
            {
                diagnostic = $"W3D HLOD '{hlod.Name}' 0x0705 aggregate produced a non-finite fixed render cost.";
                return false;
            }
        }

        return true;
    }

    public static bool TryResolveNamedRenderObjectCost(
        EnbW3dRenderResource resource,
        string renderObjectName,
        out int cost,
        out string? diagnostic)
    {
        cost = 0;
        diagnostic = null;

        if (EnbCurrentRetailRenderObjectSemantics.TryGetIntegerRenderCost(resource, renderObjectName, out cost))
            return true;

        EnbW3dRenderMesh? resolved = null;
        foreach (EnbW3dRenderMesh mesh in resource.Meshes)
        {
            if (!EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh, renderObjectName))
                continue;

            if (resolved is not null)
            {
                diagnostic = $"W3D HLOD member '{renderObjectName}' resolves to more than one mesh; current retail's level list contains one render-object pointer per member, so R5B will not invent instance multiplication.";
                return false;
            }
            resolved = mesh;
        }

        if (resolved is null)
        {
            diagnostic = $"W3D HLOD member '{renderObjectName}' does not resolve to a current EnbNext mesh render object or a proven current-retail no-op RenderObj; cost for other render-object classes remains fail-closed.";
            return false;
        }

        cost = unchecked(resolved.TriangleMetadata.Count * resolved.CurrentRetailRenderPassCount);
        return true;
    }
}

public sealed record EnbRenderDetailBudgetAllocation(
    float Budget,
    float FinalRenderCost,
    ImmutableArray<EnbW3dHlodSelection> HlodSelections);

/// <summary>
/// Polymorphic current-retail shared-budget candidate contract exposed by
/// FUN_0096F0C0/FUN_0096F180. The complete direct-caller cross-check proves
/// the shared heap is not HLOD-specific: sphere prototypes, ring prototypes,
/// particle buffers and a point-array line family register through the same
/// virtual transition interface as W3D HLODs.
///
/// Family-specific compilers remain responsible for producing the exact cost
/// and transition-priority arrays. The shared allocator only owns the common
/// level stepping and two-heap policy.
/// </summary>
// Mutable render-object detail state. The allocator reads the owner's tables
// and changes its level directly; diagnostic allocation results are detached.
public sealed class EnbRenderDetailOwner(
    string Name, int CurrentLevelOrdinal, IReadOnlyList<float> LevelCosts,
    IReadOnlyList<float> TransitionPriorities)
{
    public string Name {get;}=Name;
    public int CurrentLevelOrdinal {get;set;}=CurrentLevelOrdinal;
    public IReadOnlyList<float> LevelCosts {get;}=LevelCosts;
    public IReadOnlyList<float> TransitionPriorities {get;}=TransitionPriorities;
    public int LevelCount => LevelCosts.Count;
    public float CurrentCost => LevelCosts[CurrentLevelOrdinal];
    public float DowngradePriority => TransitionPriorities[CurrentLevelOrdinal];
    public float UpgradePriority => TransitionPriorities[CurrentLevelOrdinal+1];
}

public sealed record EnbSharedRenderDetailBudgetSelection(
    string Name,
    int LevelOrdinal);

public sealed record EnbSharedRenderDetailBudgetAllocation(
    float Budget,
    float FinalRenderCost,
    ImmutableArray<EnbSharedRenderDetailBudgetSelection> Selections);

/// <summary>
/// Current-retail shared projected-detail allocator from 0096F180. It uses two
/// one-based max-heaps over the same polymorphic candidates registered by
/// 0096F0C0:
///
///   upgrade heap   key = +priority[current + 1]
///   downgrade heap key = -priority[current]
///
/// Thus the highest-value refinement is attempted first, while over-budget work
/// removes the lowest-value removable transition first. A refinement is allowed
/// to overshoot and can be compensated by a downgrade elsewhere; current retail
/// aborts the cycle when that compensation would immediately undo the same
/// candidate it just refined.
/// </summary>
public static class EnbRenderDetailBudgetAllocator
{
    internal readonly record struct HeapEntry(int CandidateIndex, float Key);

    // One invocation owner per view. No candidate/result publication is needed
    // by the synchronous production allocator (0096F0C0/0096F180).
    public sealed class Workspace
    {
        internal HeapEntry[] Downgrade=[],Upgrade=[];
        internal bool Active;
    }

    /// <summary>
    /// HLOD compatibility facade retained for the existing handoff.
    /// HLOD compilation owns its authored minimum-level floor; once compiled,
    /// the candidate is adapted into the same polymorphic shared allocator used
    /// by every current-retail candidate family.
    /// </summary>
    public static EnbRenderDetailBudgetAllocation Allocate(
        float budget,
        IReadOnlyList<EnbHlodDetailOwner> candidates,
        float fixedRenderCost = 0f)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var sharedCandidates = new EnbRenderDetailOwner[candidates.Count];
        for (int index = 0; index < candidates.Count; index++)
        {
            EnbHlodDetailOwner candidate = candidates[index]
                ?? throw new ArgumentException("Candidate collection contains null.", nameof(candidates));
            sharedCandidates[index] = AdaptHlodCandidate(candidate);
        }

        EnbSharedRenderDetailBudgetAllocation shared = AllocateShared(
            budget,
            sharedCandidates,
            fixedRenderCost);

        var selections = ImmutableArray.CreateBuilder<EnbW3dHlodSelection>(shared.Selections.Length);
        foreach (EnbSharedRenderDetailBudgetSelection selection in shared.Selections)
            selections.Add(new EnbW3dHlodSelection(selection.Name, selection.LevelOrdinal));

        return new EnbRenderDetailBudgetAllocation(
            shared.Budget,
            shared.FinalRenderCost,
            selections.ToImmutable());
    }

    /// <summary>Preserves HLOD validation and authored floors when mixing families in one allocation.</summary>
    public static EnbRenderDetailOwner AdaptHlodCandidate(EnbHlodDetailOwner candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateHlodCandidate(candidate);
        return candidate.Shared;
    }

    /// <summary>
    /// Executes the exact shared 0096F180 heap policy without assuming which
    /// render-object family supplied a candidate. This intentionally does not
    /// synthesize sphere/ring/particle/line priorities here; those remain
    /// separate evidence slices because their +0x130 preparation semantics are
    /// family-specific.
    /// </summary>
    public static EnbSharedRenderDetailBudgetAllocation AllocateShared(
        float budget,
        IReadOnlyList<EnbRenderDetailOwner> candidates,
        float fixedRenderCost = 0f,
        Action<int,int>? levelChanged = null)
    {
        // Diagnostic/compatibility capture: production uses AllocateInPlace.
        ArgumentNullException.ThrowIfNull(candidates);
        var levels=new int[candidates.Count];
        for(int i=0;i<levels.Length;i++)levels[i]=candidates[i].CurrentLevelOrdinal;
        try
        {
            float total=AllocateInPlace(budget,candidates,new Workspace(),fixedRenderCost,levelChanged);
            var selections=ImmutableArray.CreateBuilder<EnbSharedRenderDetailBudgetSelection>(candidates.Count);
            foreach(var owner in candidates)selections.Add(new(owner.Name,owner.CurrentLevelOrdinal));
            return new(budget,total,selections.MoveToImmutable());
        }
        finally {for(int i=0;i<levels.Length;i++)candidates[i].CurrentLevelOrdinal=levels[i];}
    }

    public static float AllocateInPlace(float budget,IReadOnlyList<EnbRenderDetailOwner> candidates,
        Workspace workspace,float fixedRenderCost=0f,Action<int,int>? levelChanged=null)
    {
        if(workspace.Active)throw new InvalidOperationException("Detail workspace is already in use by another invocation.");
        workspace.Active=true;
        try {return AllocateOwned(budget,candidates,workspace,fixedRenderCost,levelChanged);}
        finally {workspace.Active=false;}
    }

    private static float AllocateOwned(float budget,IReadOnlyList<EnbRenderDetailOwner> candidates,
        Workspace workspace,float fixedRenderCost,Action<int,int>? levelChanged)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (!float.IsFinite(budget) || budget < 0f)
            throw new ArgumentOutOfRangeException(nameof(budget));
        if (!float.IsFinite(fixedRenderCost) || fixedRenderCost < 0f)
            throw new ArgumentOutOfRangeException(nameof(fixedRenderCost));

        var states = candidates;
        float totalCost = fixedRenderCost;
        for (int index = 0; index < candidates.Count; index++)
        {
            EnbRenderDetailOwner candidate = candidates[index]
                ?? throw new ArgumentException("Candidate collection contains null.", nameof(candidates));
            ValidateSharedCandidate(candidate);

            totalCost += candidate.CurrentCost;
        }

        if (states.Count != 0)
        {
            HeapEntry[] downgradeHeap = CreateHeap(states, ref workspace.Downgrade, false);
            HeapEntry[] upgradeHeap = CreateHeap(states, ref workspace.Upgrade, true);

            while (true)
            {
                int upgradedCandidateIndex = -1;

                if (budget >= totalCost)
                {
                    if (upgradeHeap[1].Key == EnbW3dHlodBudgetCompiler.CurrentRetailNoUpgradeSentinel)
                        break;

                    upgradedCandidateIndex = upgradeHeap[1].CandidateIndex;
                    EnbRenderDetailOwner upgraded = states[upgradedCandidateIndex];
                    totalCost -= upgraded.CurrentCost;
                    upgraded.CurrentLevelOrdinal++;
                    levelChanged?.Invoke(upgradedCandidateIndex,upgraded.CurrentLevelOrdinal);
                    totalCost += upgraded.CurrentCost;

                    upgradeHeap[1] = new HeapEntry(upgradedCandidateIndex, upgraded.UpgradePriority);
                    SiftDown(upgradeHeap, 1, states.Count);
                    UpdateCandidateKey(
                        downgradeHeap,
                        upgradedCandidateIndex,
                        -upgraded.DowngradePriority,
                        states.Count);

                    if (totalCost <= budget)
                        continue;
                }

                while (true)
                {
                    if (downgradeHeap[1].Key == -EnbW3dHlodBudgetCompiler.CurrentRetailNoDowngradeSentinel)
                        goto Finished;

                    int downgradedCandidateIndex = downgradeHeap[1].CandidateIndex;
                    EnbRenderDetailOwner downgraded = states[downgradedCandidateIndex];
                    totalCost -= downgraded.CurrentCost;
                    downgraded.CurrentLevelOrdinal--;
                    levelChanged?.Invoke(downgradedCandidateIndex,downgraded.CurrentLevelOrdinal);
                    totalCost += downgraded.CurrentCost;

                    downgradeHeap[1] = new HeapEntry(downgradedCandidateIndex, -downgraded.DowngradePriority);
                    SiftDown(downgradeHeap, 1, states.Count);
                    UpdateCandidateKey(
                        upgradeHeap,
                        downgradedCandidateIndex,
                        downgraded.UpgradePriority,
                        states.Count);

                    if (upgradedCandidateIndex == downgradedCandidateIndex)
                        goto Finished;

                    if (totalCost <= budget)
                        break;
                }
            }
        }

    Finished:
        return totalCost;
    }

    private static void ValidateHlodCandidate(EnbHlodDetailOwner candidate)
    {
        if ((uint)candidate.MinimumLevelOrdinal >= (uint)candidate.LevelCount ||
            candidate.CurrentLevelOrdinal < candidate.MinimumLevelOrdinal)
        {
            throw new ArgumentException($"HLOD candidate '{candidate.Name}' has an invalid current/minimum level state.", nameof(candidate));
        }
    }

    private static void ValidateSharedCandidate(EnbRenderDetailOwner candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Name))
            throw new ArgumentException("Shared render-detail candidate name is required.", nameof(candidate));
        if (candidate.LevelCount == 0 || candidate.TransitionPriorities.Count != candidate.LevelCount + 1)
            throw new ArgumentException($"Shared render-detail candidate '{candidate.Name}' has inconsistent level/priority arrays.", nameof(candidate));
        if ((uint)candidate.CurrentLevelOrdinal >= (uint)candidate.LevelCount)
            throw new ArgumentException($"Shared render-detail candidate '{candidate.Name}' has an invalid current level state.", nameof(candidate));
        if (candidate.TransitionPriorities[0] != EnbW3dHlodBudgetCompiler.CurrentRetailNoDowngradeSentinel)
            throw new ArgumentException($"Shared render-detail candidate '{candidate.Name}' is missing the current-retail no-downgrade sentinel.", nameof(candidate));
        if (candidate.TransitionPriorities[candidate.LevelCount] != EnbW3dHlodBudgetCompiler.CurrentRetailNoUpgradeSentinel)
            throw new ArgumentException($"Shared render-detail candidate '{candidate.Name}' is missing the current-retail no-upgrade sentinel.", nameof(candidate));

        for (int index = 0; index < candidate.LevelCosts.Count; index++)
        {
            float cost = candidate.LevelCosts[index];
            if (!float.IsFinite(cost) || cost < 0f)
                throw new ArgumentException($"Shared render-detail candidate '{candidate.Name}' has invalid level cost {cost} at index {index}.", nameof(candidate));
        }
    }

    private static HeapEntry[] CreateHeap(IReadOnlyList<EnbRenderDetailOwner> states,
        ref HeapEntry[] heap,bool upgrade)
    {
        if(heap.Length<states.Count+1)Array.Resize(ref heap,Math.Max(states.Count+1,heap.Length*2));
        for(int index=0;index<states.Count;index++)
            heap[index+1]=new(index,upgrade?states[index].UpgradePriority:-states[index].DowngradePriority);
        for(int index=states.Count/2;index>0;index--)SiftDown(heap,index,states.Count);
        return heap;
    }

    private static void UpdateCandidateKey(
        HeapEntry[] heap,
        int candidateIndex,
        float newKey,
        int count)
    {
        for (int index = 1; index <= count; index++)
        {
            if (heap[index].CandidateIndex != candidateIndex)
                continue;

            float oldKey = heap[index].Key;
            heap[index] = new HeapEntry(candidateIndex, newKey);
            if (oldKey <= newKey)
                SiftUp(heap, index);
            else
                SiftDown(heap, index, count);
            return;
        }

        throw new InvalidOperationException("Current-retail shared render-detail heap lost a registered candidate.");
    }

    private static void SiftUp(HeapEntry[] heap, int index)
    {
        HeapEntry value = heap[index];

        // FUN_0096F530 writes float.MaxValue into the one-based heap's slot-0
        // parent sentinel, not into the root. That detail is what lets a key
        // update at index 1 terminate immediately instead of corrupting the
        // root while preserving the helper's >= parent tie behavior.
        heap[0] = heap[0] with { Key = float.MaxValue };

        while (true)
        {
            int parent = index / 2;
            if (value.Key < heap[parent].Key)
                break;

            heap[index] = heap[parent];
            index = parent;
        }

        heap[index] = value;
    }

    private static void SiftDown(HeapEntry[] heap, int index, int count)
    {
        HeapEntry value = heap[index];
        if (index <= count / 2)
        {
            int child;
            do
            {
                child = index * 2;
                if (child < count && heap[child].Key < heap[child + 1].Key)
                    child++;

                if (heap[child].Key <= value.Key)
                    break;

                heap[index] = heap[child];
                index = child;
            }
            while (child <= count / 2);
        }

        heap[index] = value;
    }
}
