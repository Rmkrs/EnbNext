using System.Collections.Immutable;
using System.Runtime.InteropServices;
namespace EnbNext.Rendering;

// Model-lifetime inputs for 0098EA70/0098EE30. Inputs are copied at binding;
// each owner updates its priorities in place for its synchronous view traversal.
public sealed class EnbPreparedHlodBudget
{
    private readonly string name;
    private readonly EnbCurrentRetailHlodDetailRuntime.Level[] levels;
    private EnbPreparedHlodBudget(string name,EnbCurrentRetailHlodDetailRuntime.Level[] levels)
    {
        this.name=name;this.levels=levels;costs=new float[levels.Length];priorities=new float[levels.Length+1];
        owner=new(name,0,0,costs,priorities,0);
    }
    private readonly float[] costs,priorities;
    private readonly EnbHlodDetailOwner owner;
    public static bool TryPrepare(EnbW3dRenderHlod hlod,IReadOnlyList<int> activeLevelRenderCosts,
        out EnbPreparedHlodBudget prepared,out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(hlod);ArgumentNullException.ThrowIfNull(activeLevelRenderCosts);
        prepared=null!;diagnostic=null;
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


        var levels=new EnbCurrentRetailHlodDetailRuntime.Level[hlod.Levels.Count];
        for(int i=0;i<levels.Length;i++)levels[i]=EnbCurrentRetailHlodDetailRuntime.FromCost(hlod.Levels[i].Threshold,activeLevelRenderCosts[i]);
        prepared=new(hlod.Name,levels);return true;
    }
    public bool TryUpdate(int currentLevelOrdinal,EnbResolvedRenderCamera camera,
        EnbW3dWorldBounds bounds,float lodScale,out EnbHlodDetailOwner candidate,out string? diagnostic)
    {
        candidate=null!;diagnostic=null;
        if(!float.IsFinite(lodScale)){diagnostic=$"W3D HLOD '{name}' received a non-finite LOD scale.";return false;}
        if(!EnbCurrentRetailProjectedDetail.TryCompute(camera,bounds,out float projectedDetail))
        {diagnostic=$"W3D HLOD '{name}' produced an invalid current-retail projected-detail metric.";return false;}
        int minimum=EnbCurrentRetailHlodDetailRuntime.UpdateTables(levels,projectedDetail,lodScale,costs,priorities);
        owner.CurrentLevelOrdinal=Math.Max(Math.Clamp(currentLevelOrdinal,0,levels.Length-1),minimum);
        owner.MinimumLevelOrdinal=minimum;owner.ProjectedDetail=projectedDetail;
        candidate=owner;return true;
    }
}
