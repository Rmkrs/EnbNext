using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbDetailOwnerTests
{
    [Fact]
    public void RetainedParticleTablesMatchDetachedCompilerAcrossChangingViews()
    {
        for(int mode=0;mode<7;mode++)
        for(int count=1;count<=17;count++)
        {
            var owner=new EnbParticleBufferDetailBudgetSemantics.Owner("buffer",93,mode,count);
            var identity=owner.Detail;
            for(int frame=0;frame<40;frame++)
            {
                float detail=frame*1.375f,scale=frame%3==0?0f:0.75f;
                var expected=EnbParticleBufferDetailBudgetSemantics.CreateCandidate("buffer",frame%20-2,93,mode,detail,scale,count);
                var actual=owner.Update(frame%20-2,detail,scale);
                Assert.Same(identity,actual);
                Assert.Equal(expected.CurrentLevelOrdinal,actual.CurrentLevelOrdinal);
                Assert.Equal(expected.LevelCosts.ToArray(),actual.LevelCosts.ToArray());
                Assert.Equal(expected.TransitionPriorities.ToArray(),actual.TransitionPriorities.ToArray());
            }
        }
    }

    [Fact]
    public void InPlaceAllocationPreservesTransitionsAndAllocatesNothingAfterWarmup()
    {
        var first=new EnbParticleBufferDetailBudgetSemantics.Owner("first",200,1);
        var second=new EnbParticleBufferDetailBudgetSemantics.Owner("second",200,1);
        EnbRenderDetailOwner[] owners=[first.Detail,second.Detail];
        var workspace=new EnbRenderDetailBudgetAllocator.Workspace();
        var expectedTransitions=new List<(int,int)>();var actualTransitions=new List<(int,int)>();
        for(int i=0;i<40;i++)
        {
            first.Update(5,300+i);second.Update(7,300-i);
            expectedTransitions.Clear();actualTransitions.Clear();
            var expected=EnbRenderDetailBudgetAllocator.AllocateShared(150+i,owners,17,(n,l)=>expectedTransitions.Add((n,l)));
            Assert.Equal(5,first.Detail.CurrentLevelOrdinal);Assert.Equal(7,second.Detail.CurrentLevelOrdinal);
            float cost=EnbRenderDetailBudgetAllocator.AllocateInPlace(150+i,owners,workspace,17,(n,l)=>actualTransitions.Add((n,l)));
            Assert.Equal(expected.FinalRenderCost,cost);Assert.Equal(expectedTransitions,actualTransitions);
            Assert.Equal(expected.Selections[0].LevelOrdinal,owners[0].CurrentLevelOrdinal);
            Assert.Equal(expected.Selections[1].LevelOrdinal,owners[1].CurrentLevelOrdinal);
        }
        for(int i=0;i<100;i++){first.Update(5,300);second.Update(7,200);EnbRenderDetailBudgetAllocator.AllocateInPlace(150,owners,workspace);}
        long start=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++){first.Update(5,300+i);second.Update(7,200+i);EnbRenderDetailBudgetAllocator.AllocateInPlace(150,owners,workspace);}
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-start);
    }

    [Fact]
    public void RecursiveInvocationNeedsItsOwnWorkspaceAndFailureReleasesWorkspace()
    {
        EnbRenderDetailOwner[] owners=[new("first",0,[10,20],[float.MaxValue,1,-1])];
        var workspace=new EnbRenderDetailBudgetAllocator.Workspace();
        Assert.Throws<InvalidOperationException>(()=>EnbRenderDetailBudgetAllocator.AllocateInPlace(20,owners,workspace,
            levelChanged:(n,l)=>EnbRenderDetailBudgetAllocator.AllocateInPlace(20,owners,workspace)));
        owners[0].CurrentLevelOrdinal=0;
        Assert.Equal(20,EnbRenderDetailBudgetAllocator.AllocateInPlace(20,owners,workspace));
    }
}
