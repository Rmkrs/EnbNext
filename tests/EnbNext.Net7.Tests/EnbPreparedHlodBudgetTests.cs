using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;
[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbPreparedHlodBudgetTests
{
    private static EnbResolvedRenderCamera Camera()
    {
        Assert.True(EnbRenderViewport.TryFromLogicalSize(1000,500,1,out var viewport));
        Assert.True(EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,viewport,Math.PI/2,new(1,10000),out var camera));return camera;
    }
    private static EnbW3dRenderHlod Hlod(int count)=>new("prepared","",Enumerable.Range(0,count)
        .Select(i=>new EnbW3dRenderHlodLevel(i,i==1?float.NaN:i*.002f,0,[])).ToArray());
    private static void Equal(EnbHlodDetailOwner a,EnbHlodDetailOwner b)
    {
        Assert.Equal(a.Name,b.Name);Assert.Equal(a.CurrentLevelOrdinal,b.CurrentLevelOrdinal);Assert.Equal(a.MinimumLevelOrdinal,b.MinimumLevelOrdinal);
        Assert.Equal(BitConverter.SingleToUInt32Bits(a.ProjectedDetail),BitConverter.SingleToUInt32Bits(b.ProjectedDetail));
        Assert.Equal(a.LevelCosts.Select(BitConverter.SingleToUInt32Bits),b.LevelCosts.Select(BitConverter.SingleToUInt32Bits));
        Assert.Equal(a.TransitionPriorities.Select(BitConverter.SingleToUInt32Bits),b.TransitionPriorities.Select(BitConverter.SingleToUInt32Bits));
    }
    [Fact] public void ProductionOwnerUpdatesInPlaceWithoutAllocations()
    {
        var hlod=Hlod(4);int[] costs=[0,10,20,40];var camera=Camera();
        Assert.True(EnbPreparedHlodBudget.TryPrepare(hlod,costs,out var prepared,out _));
        prepared.TryUpdate(1,camera,new(new(100,0,0),10),1,out var owner,out _);
        for(int i=0;i<100;i++)
        {
            var bounds=new EnbW3dWorldBounds(new(100+i,0,0),10);
            Assert.True(prepared.TryUpdate(i%4,camera,bounds,1,out var actual,out _));
            Assert.Same(owner,actual);
            EnbW3dHlodBudgetCompiler.TryCreateCandidate(hlod,i%4,camera,bounds,costs,1,out var expected,out _);
            Equal(expected,actual);
        }
        long start=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++)prepared.TryUpdate(i%4,camera,new(new(100+i,0,0),10),1,out _,out _);
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-start);
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public void InvalidStaticInputsPreserveDiagnostic(int mode)
    {
        var hlod=Hlod(mode);int[] costs=mode==2?[0,-1]:new int[mode];
        Assert.False(EnbPreparedHlodBudget.TryPrepare(hlod,costs,out _,out var actual));
        Assert.False(EnbW3dHlodBudgetCompiler.TryCreateCandidate(hlod,0,Camera(),new(new(100,0,0),10),costs,1,out _,out var expected));
        Assert.Equal(expected,actual);
    }
}
