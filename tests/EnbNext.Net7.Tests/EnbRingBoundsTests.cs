using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRingBoundsTests
{
    [Fact]
    public void SharedSceneAllocationRetainsHlodFloorAndChargesRingAndFixedCostsTogether()
    {
        var hlod=new EnbHlodDetailOwner("ship",2,1,[10,20,80],[float.MaxValue,float.MaxValue,10,-1],100);
        var adapted=EnbRenderDetailBudgetAllocator.AdaptHlodCandidate(hlod);
        var ring=EnbSphereRingDetailBudgetSemantics.CreateRingCandidate("ring",20,100);
        var limited=EnbRenderDetailBudgetAllocator.AllocateShared(50,[adapted,ring],7);
        Assert.InRange(limited.FinalRenderCost,27,50);
        Assert.InRange(limited.Selections[0].LevelOrdinal,1,2);
        Assert.True(limited.Selections[1].LevelOrdinal<20);
        var zero=EnbRenderDetailBudgetAllocator.AllocateShared(0,[adapted,ring],7);
        Assert.Equal(27,zero.FinalRenderCost);
        Assert.Equal(1,zero.Selections[0].LevelOrdinal);
        Assert.Equal(0,zero.Selections[1].LevelOrdinal);
        Assert.Throws<ArgumentException>(()=>EnbRenderDetailBudgetAllocator.AdaptHlodCandidate(new EnbHlodDetailOwner(hlod.Name,0,hlod.MinimumLevelOrdinal,hlod.LevelCosts,hlod.TransitionPriorities,hlod.ProjectedDetail)));
    }


}
