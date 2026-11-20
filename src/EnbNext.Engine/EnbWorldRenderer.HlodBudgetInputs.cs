using System.Collections.Immutable;
using EnbNext.Rendering;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private long _preparedHlodBudgetUses,_preparedHlodBudgetBuilds;
    private sealed record OwnedHlodBudget(LiveCpuRoot Root,EnbW3dRenderHlod Hlod,long AssetGeneration,ImmutableArray<int> Costs,EnbPreparedHlodBudget Inputs);
    private long _preparedHlodCostBuilds,_preparedHlodCostUses;
    private sealed class OwnedHlodCosts
    {
        public readonly LiveCpuRoot Root;
        public readonly long AssetGeneration;
        public readonly ImmutableArray<int> Levels;
        private readonly bool recursive;
        private readonly EnbWorldRenderer view;
        private readonly LiveRootInstance instance;
        public LiveCpuRoot Retained;
        public readonly Func<EnbW3dRenderHlodMember,bool> IsActive;
        public OwnedHlodCosts(EnbWorldRenderer view,LiveRootInstance instance,long generation,
            ImmutableArray<int> levels,bool recursive)
        {
            this.view=view;this.instance=instance;Root=instance.Root;Retained=Root;
            AssetGeneration=generation;Levels=levels;this.recursive=recursive;IsActive=TestActive;
        }
        private bool TestActive(EnbW3dRenderHlodMember member)
        {
            if(!view.RetainedEnginePivotVisible(instance,member.ReferenceIndex))return false;
            if(recursive)
            {
                var children=Retained.Resource.CompositeRenderChildren;
                for(int i=0;i<children.Count;i++)
                    if(string.Equals(children[i].Name,member.Name,StringComparison.OrdinalIgnoreCase))return false;
            }
            return true;
        }
    }
    private bool TryResolveOwnedHlodCosts(LiveRootInstance instance,out ImmutableArray<int> levels,
        out float aggregate,out string? diagnostic)
    {
        var prepared=instance.PreparedHlodCosts;
        if(prepared is null||!ReferenceEquals(prepared.Root,instance.Root)||prepared.AssetGeneration!=_assetGeneration)
        {
            bool recursive=instance.Root.AuthoredGlobalOwner||instance.Root.HmodelChildren.Any(child=>child.IsAggregate);
            if(!TryResolveHlodCompiledCosts(instance.Root.Resource,instance.Root.Hlod!,recursive,out levels,out aggregate,out diagnostic))return false;
            instance.PreparedHlodCosts=prepared=new(this,instance,_assetGeneration,levels,recursive);
            _preparedHlodCostBuilds++;
        }
        // Only constructor levels are retained. Live aggregate membership and
        // engine pivots are reevaluated in the original order on every call.
        prepared.Retained=RetainedDrawRoot(instance);levels=prepared.Levels;_preparedHlodCostUses++;
        return EnbW3dHlodBudgetCompiler.TryComputeW3dAggregateFixedRenderCost(prepared.Retained.Resource,
            prepared.Retained.Hlod!,prepared.IsActive,out aggregate,out diagnostic);
    }
    private bool TryCreateOwnedHlodBudgetCandidate(LiveRootInstance instance,EnbW3dRenderHlod hlod,int currentLevelOrdinal,
        EnbResolvedRenderCamera camera,EnbW3dWorldBounds bounds,ImmutableArray<int> activeLevelRenderCosts,float lodScale,
        out EnbHlodDetailOwner candidate,out string? diagnostic)
    {
        var prepared=instance.PreparedHlodBudget;
        if(prepared is null||!ReferenceEquals(prepared.Root,instance.Root)||!ReferenceEquals(prepared.Hlod,hlod)||
            prepared.AssetGeneration!=_assetGeneration||!prepared.Costs.Equals(activeLevelRenderCosts))
        {
            if(!EnbPreparedHlodBudget.TryPrepare(hlod,activeLevelRenderCosts,out var inputs,out _))
                return EnbW3dHlodBudgetCompiler.TryCreateCandidate(hlod,currentLevelOrdinal,camera,bounds,activeLevelRenderCosts,lodScale,out candidate,out diagnostic);
            instance.PreparedHlodBudget=prepared=new(instance.Root,hlod,_assetGeneration,activeLevelRenderCosts,inputs);
            _preparedHlodBudgetBuilds++;
        }
        _preparedHlodBudgetUses++;
        return prepared.Inputs.TryUpdate(currentLevelOrdinal,camera,bounds,lodScale,out candidate,out diagnostic);
    }
}
