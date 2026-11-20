using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRadiusSoundRuntimeTests
{
    [Fact]
    public void PassRadiusUsesNativeVirtualPositionBeforeCameraPlacement()
    {
        var catalog=EnbR10EffectSoundTests.Catalog("[EFFECT-1]\nEffectClass=PassEffect\nSFX=x.wav\nSFX2D=TRUE\nTriggerSFXRadius=10\nDuration=INFINITE");
        var node=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([EnbR10EffectSoundTests.Root()],catalog,0).SoundNodes);
        var runtime=new EnbRadiusSoundRuntime(node.Definition,0);
        // 006E6C66 calls the concrete getter; Pass006EDB70 returns its retained
        // matrix translation, not the live object's centre or camera overlay.
        Assert.False(runtime.Resolve(node,_=>Vector3.Zero,null,out _));
        Assert.True(runtime.Resolve(node,_=>Vector3.Zero,null,out _,_=>new(20,0,0)));
        Assert.Equal(new Vector3(20,0,0),runtime.Position);
        uint rng=0;int attempts=0;
        Assert.False(runtime.TryAttempt(0,Vector3.Zero,()=>attempts++,ref rng));
        Assert.True(runtime.TryAttempt(0,new(20,0,0),()=>attempts++,ref rng));
        Assert.Equal(1,attempts);
        Assert.True(runtime.Resolve(node,_=>Vector3.Zero,null,out _,_=>new(21,2,3)));
        Assert.Equal(new Vector3(21,2,3),runtime.Position);
        Assert.True(runtime.Resolve(node,_=>Vector3.Zero,null,out _,_=>null));
        Assert.Equal(new Vector3(21,2,3),runtime.Position);
        runtime.Schedule.Retire();
        Assert.False(runtime.TryAttempt(uint.MaxValue,new(21,2,3),()=>Assert.Fail("Retired"),ref rng));
    }
    [Theory]
    [InlineData(15000,0,0,true)]
    [InlineData(15000,1,0,true)] // Explicit replacement binary32 policy.
    [InlineData(15001,0,0,false)]
    [InlineData(0,0,0,true)]
    [InlineData(9000,12000,0,true)]
    public void ExplicitInclusiveBinary32Distance(float x,float y,float z,bool expected)
        => Assert.Equal(expected,EnbRadiusSoundRuntime.IsInside(new(x,y,z),Vector3.Zero,15000));

    [Fact]
    public void TwoDimensionalVoiceStillNeedsWorldPositionAndRetainsNontrackingBinding()
    {
        var catalog=EnbR10EffectSoundTests.Catalog("[EFFECT-1]\nEffectClass=SoundEffect\nSFX=x.wav\nSFX2D=TRUE\nTrackSource=FALSE\nTriggerSFXRadius=10\nTriggerSFXMinElapsedTime=.1\nTriggerSFXMaxElapsedTime=.1\nDuration=INFINITE");
        var node=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([EnbR10EffectSoundTests.Root()],catalog,100).SoundNodes);
        var runtime=new EnbRadiusSoundRuntime(node.Definition,100);
        Assert.False(runtime.Resolve(node,_=>null,null,out _));
        Assert.Null(runtime.Position);
        Assert.True(runtime.Resolve(node,_=>new(20,0,0),null,out _));
        Assert.True(runtime.Resolve(node,_=>Vector3.Zero,null,out _));
        Assert.Equal(new Vector3(20,0,0),runtime.Position);
        uint rng=0; int attempts=0;
        Assert.False(runtime.TryAttempt(100,Vector3.Zero,()=>attempts++,ref rng));
        Assert.Equal(100u,runtime.Schedule.NextDeadline);
        Assert.True(runtime.TryAttempt(200,new(20,0,0),()=>attempts++,ref rng));
        Assert.Equal(300u,runtime.Schedule.NextDeadline);
        Assert.False(runtime.TryAttempt(299,new(20,0,0),()=>attempts++,ref rng));
        Assert.Equal(1,attempts);
    }

    [Fact]
    public void TrackingRetainsLastKnownPositionAndRetirementIsTerminal()
    {
        var catalog=EnbR10EffectSoundTests.Catalog("[EFFECT-1]\nEffectClass=SoundEffect\nSFX=x.wav\nTriggerSFXRadius=10\nDuration=INFINITE");
        var node=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([EnbR10EffectSoundTests.Root()],catalog,0).SoundNodes);
        var runtime=new EnbRadiusSoundRuntime(node.Definition,0);
        Assert.True(runtime.Resolve(node,_=>new(2,3,4),null,out _));
        Assert.True(runtime.Resolve(node,_=>new(3,4,5),null,out _));
        Assert.True(runtime.Resolve(node,_=>null,null,out _));
        Assert.Equal(new Vector3(3,4,5),runtime.Position);
        runtime.Schedule.Retire(); uint rng=0;
        Assert.False(runtime.TryAttempt(0,Vector3.Zero,()=>Assert.Fail("Retired"),ref rng));
    }
}
