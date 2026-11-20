using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;
[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbOwnedSpatialPoseTests
{
    [Fact]
    public void ClockAndVelocityMutateWithoutPublishingOrAllocating()
    {
        var owner=new EnbCurrentRetailSpatialTransformRuntime.Owner();
        var pose=new EnbRenderPose(true,new(0,Vector3.Zero,Quaternion.Identity,default,0,default));
        var view=owner.Update(pose);
        long start=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++)owner.Update(pose with {Sample=pose.Sample with {ClientTime=(uint)i,Velocity=new(i,2,3)}});
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-start);
        Assert.Same(owner,view);Assert.Equal(new Vector3(9999,2,3),view.Velocity);
    }
    [Fact]
    public void OwnerMutatesInPlaceAndExplicitCopiesRemainDetached()
    {
        var owner=new EnbCurrentRetailSpatialTransformRuntime.Owner();
        var original=new EnbSceneTransformSnapshot(1,new(2,3,4),new(.1f,.2f,.3f,.9f),default,0,default,default);
        var input=EnbRenderPose.FromTransform(original);
        var first=owner.Update(input);
        for(int i=0;i<100;i++)Assert.Same(first,owner.Update(input));
        Assert.Equal(1,owner.TransformUpdates);Assert.Same(owner,first);
        var detached=first.CaptureDiagnostics();
        var clock=owner.Update(EnbRenderPose.FromTransform(original with {ClientTime=2,Velocity=new(1,2,3)}));
        Assert.Equal(1,owner.TransformUpdates);Assert.Same(owner,first);Assert.Same(first,clock);
        Assert.True(EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(original,out var expected,out _));
        Assert.True(EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(clock,out var same,out _));Assert.Equal(expected,same);
        var moved=owner.Update(EnbRenderPose.FromTransform(original with {Position=new(5,6,7)}));
        Assert.Equal(2,owner.TransformUpdates);
        Assert.True(EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(detached,out var retained,out _));Assert.Equal(expected,retained);
        Assert.True(EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(moved,out var world,out _));Assert.Equal(new Vector3(5,6,7),world.Translation);
        // Functional copies cannot reuse the source publication's bound result.
        var clone=moved.CaptureDiagnostics() with {Position=new(8,9,10)};
        Assert.True(EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(clone,out var changed,out _));Assert.Equal(clone.Position,changed.Translation);
    }
    [Fact]
    public void ProviderAndInvalidPoseChangesUseTheExistingRetailRepair()
    {
        var owner=new EnbCurrentRetailSpatialTransformRuntime.Owner();
        var source=new EnbSceneTransformSnapshot(1,new(2,3,4),Quaternion.Identity,default,0,default,default)
            {ProviderTransform=new Matrix4x4(2,0,0,0,0,3,0,0,0,0,4,0,8,9,10,1)};
        var bound=owner.Update(EnbRenderPose.FromTransform(source));
        bool expected=EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(source,out var world,out var error);
        Assert.Equal(expected,EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(bound,out var actual,out var diagnostic));
        Assert.Equal(world,actual);Assert.Equal(error,diagnostic);
        var invalid=source with {ProviderTransform=null,Position=new(float.NaN,0,0)};
        bound=owner.Update(EnbRenderPose.FromTransform(invalid));
        Assert.False(EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(bound,out _,out _));
        var invalidAgain=owner.Update(EnbRenderPose.FromTransform(invalid));Assert.Same(bound,invalidAgain);
        Assert.Equal(2,owner.TransformUpdates);
        var negativeZero=source with {ProviderTransform=null,Position=new(-0f,0,0)};
        owner.Update(EnbRenderPose.FromTransform(negativeZero));long before=owner.TransformUpdates;
        owner.Update(EnbRenderPose.FromTransform(negativeZero with {Position=Vector3.Zero}));Assert.Equal(before+1,owner.TransformUpdates);
    }
}
