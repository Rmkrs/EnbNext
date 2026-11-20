using EnbNext.Presentation;
namespace EnbNext.Net7.Tests;
[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbRenderPoseAllocationTests
{
    [Fact]
    public void RepeatedOwnerMutationAllocatesNothingAndDiagnosticCaptureIsExplicit()
    {
        var owners=new EnbRenderPoseOwner?[1024];
        for(int i=0;i<owners.Length;i++)owners[i]=new();
        var poses=new EnbRenderPoseSet(owners);
        poses.WriteWarp(7,3,2,[]);
        var captured=poses.Detach();
        for(int i=0;i<100;i++){poses.WriteWarp(7,i,i+1,[]);poses.WritePrivateWarp(7,[]);}
        long start=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++){poses.WriteWarp(7,i,i+1,[]);poses.WritePrivateWarp(7,[]);}
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-start);
        Assert.Equal(9999,poses[7].EngineWarpState);
        Assert.Equal(3,captured[7].EngineWarpState);
        Assert.False(captured[7].HasPrivateWarpInputs);
        Assert.Same(owners[7],poses.Owner(7));
    }
}
