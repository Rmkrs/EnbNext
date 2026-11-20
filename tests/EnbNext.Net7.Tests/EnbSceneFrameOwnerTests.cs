using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using EnbNext.Net7.World;
using EnbNext.Presentation;
namespace EnbNext.Net7.Tests;
[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbSceneFrameOwnerTests
{
    private static (Net7WorldState World,EnbPresentationSnapshotPublisher Publisher) Create()
    {
        var world=new Net7WorldState();
        var obj=world.MaterializeCreate(new(7,1,10,1,0,0,0));
        obj.Spatial=new(){Kind=Net7SpatialProviderKind.Simple,Model=new Net7SimpleSpatialModel(1000,new(1,2,3),Quaternion.Identity,new(.1f,.2f,.3f))};
        var publisher=new EnbPresentationSnapshotPublisher();publisher.Publish(world,null,1000);
        return(world,publisher);
    }
    private static int Buffers(EnbPresentationSnapshotPublisher publisher)
    {
        var sampler=typeof(EnbPresentationSnapshotPublisher).GetField("_frameSampler",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(publisher)!;
        return (int)sampler.GetType().GetProperty("BufferCount",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(sampler)!;
    }
    [Fact]
    public void NormalHandoffUsesTwoBuffersAndRetainsPriorPhysicalValues()
    {
        var(world,publisher)=Create();EnbSceneFrame? previous=null;
        for(uint time=1000;time<1300;time++)
        {
            var old=previous?.Objects[0].Spatial!.Transform.Position;
            var next=publisher.AcquireLiveFrame(world,time,null)!;
            if(previous is not null){Assert.NotSame(previous,next);Assert.Equal(old,previous.Objects[0].Spatial!.Transform.Position);previous.Release();}
            Assert.Equal(time,next.ClientTime);previous=next;
        }
        previous!.Release();Assert.Equal(2,Buffers(publisher));
    }
    [Fact]
    public void WarmSamplingAndNumericVisualPreparationAllocateZero()
    {
        var(world,publisher)=Create();
        for(uint time=1000;time<2000;time++){var frame=publisher.AcquireLiveFrame(world,time,null)!;frame.PrepareVisualFrame();frame.Release();}
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(uint time=2000;time<12000;time++){var frame=publisher.AcquireLiveFrame(world,time,null)!;frame.PrepareVisualFrame();frame.Release();}
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);Assert.Equal(2,Buffers(publisher));
    }
    [Fact]
    public void QueuedVisualAndNestedViewProtectTheirPhysicalBuffer()
    {
        var(world,publisher)=Create();var physical=publisher.AcquireLiveFrame(world,1000,null)!;
        var visual=physical.PrepareVisualFrame();var nested=new EnbSceneFramePool().Prepare(visual);nested.Retain();
        var original=physical.Objects[0].Spatial!.Transform.Position;physical.Release();
        for(uint time=1001;time<1010;time++){var next=publisher.AcquireLiveFrame(world,time,null)!;Assert.NotSame(physical,next);next.Release();}
        Assert.Equal(original,physical.Objects[0].Spatial!.Transform.Position);
        Assert.Throws<InvalidOperationException>(()=>physical.PrepareVisualFrame());
        nested.Release();Assert.False(physical.IsRetained);Assert.False(visual.IsRetained);
        Assert.Throws<InvalidOperationException>(()=>nested.Release());
    }
    [Fact]
    public void IndependentVisualTransformAndExplicitCaptureDoNotChangePhysicalState()
    {
        var(world,publisher)=Create();var physical=publisher.AcquireLiveFrame(world,1000,null)!;
        var visual=physical.PrepareVisualFrame();var original=physical.Objects[0].Spatial!.Transform.Position;
        var transform=EnbRenderPose.FromTransform(physical.Objects[0].Spatial!.Transform).ToTransform() with {Position=new(100,200,300)};
        visual.ApplyTransform(visual.Objects[0].RenderIdentity,transform);
        Assert.Equal(original,physical.Objects[0].Spatial!.Transform.Position);
        var capture=EnbSceneCapture.CaptureDiagnostics(visual);
        physical.Release();
        for(uint time=1001;time<1010;time++){var frame=publisher.AcquireLiveFrame(world,time,null)!;frame.PrepareVisualFrame();frame.Release();}
        Assert.Equal(new Vector3(100,200,300),capture.Objects[0].Spatial!.Transform.Position);
    }
    [Fact]
    public void ResetReleasesRetiredFrameGraphAfterFinalConsumer()
    {
        var weak=Retired();
        for(int i=0;i<3&&weak.IsAlive;i++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
        Assert.False(weak.IsAlive);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Retired()
    {
        var(world,publisher)=Create();var physical=publisher.AcquireLiveFrame(world,1000,null)!;
        var visual=physical.PrepareVisualFrame();visual.Retain();physical.Release();publisher.Clear();
        Assert.True(physical.IsRetained);visual.Release();return new WeakReference(physical);
    }
}
