using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSpatialSnapshotReuseTests
{
    [Fact]
    public void RepeatedSnapshotPreservesRepairAndNewSnapshotChangesTransform()
    {
        var pose=new EnbSceneTransformSnapshot(1,new(2,3,4),new(.1f,.2f,.3f,.9f),default,0,default,default);
        Assert.True(EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(pose.Position,pose.Orientation,out var expected,out _));
        for(int i=0;i<3;i++)
        {Assert.True(EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(pose,out var actual,out _));Assert.Equal(expected,actual);}
        var moved=pose with {Position=new(5,6,7)};
        Assert.True(EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(moved,out var movedWorld,out _));
        Assert.Equal(moved.Position,movedWorld.Translation);
        var provider=moved with {ProviderTransform=Matrix4x4.CreateTranslation(8,9,10)};
        Assert.True(EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(provider,out var providerWorld,out _));
        Assert.Equal(new Vector3(8,9,10),providerWorld.Translation);
        var invalid=pose with {Position=new(float.NaN,0,0)};
        for(int i=0;i<2;i++)Assert.False(EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(invalid,out _,out _));
        Assert.True(EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(pose,out var unchanged,out _));
        Assert.Equal(expected,unchanged);
    }
}
