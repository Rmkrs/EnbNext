using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10FiniteInheritedParticleTests
{
    [Fact]
    public void FiniteExhaustNamedAttachmentUsesCommonAdmissionAndDrains()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var asset=EnbCurrentRetailShipExhaustAsset.Load(assets,"BST12-p4");
        Assert.True(asset.IsReady,asset.Error??asset.DeferredReason);
        Assert.Equal(3,asset.Definition.MaximumEmissions);
        var state=new EnbRetailParticleSceneLifetime(asset.Definition,0);
        state.Prepare(0,Matrix4x4.Identity,1,true,true);
        state.Callback(0,0,Matrix4x4.Identity,1);
        state.Callback(100,100,Matrix4x4.CreateTranslation(10,0,0),1);
        state.AdvanceForRender(100);
        Assert.Equal(3,state.Emitter.EmittedCount);
        Assert.Equal(3,state.BorrowLiveBirths().Count);
        state.Destroy(100,Matrix4x4.CreateTranslation(10,0,0),1);
        state.Callback(1000,900,Matrix4x4.Identity,1);
        state.AdvanceForRender(1000);
        Assert.Equal(3,state.Emitter.EmittedCount);
        Assert.True(state.CanRetire);
    }
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void WraithEmittersRetainBirthMotionAndPruneExpiredParticles(int index)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        Assert.True(assets.RetailMixResources!.TryReadResource($"mwrkblood{index:00}.w3d",out var bytes,out _));
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes,out var definition,out var error),error);
        var state=EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(definition);
        void Advance(uint age,uint delta,float x,bool visible=true,bool initial=false) =>
            EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,definition,age,delta,
                Matrix4x4.CreateScale(22)*Matrix4x4.CreateTranslation(x,0,0),visible,22,initial);
        Advance(0,0,0,initial:true);
        uint step=(uint)(1000d/definition.EmissionRatePerSecond)+1;
        Advance(step,step,step);
        Assert.NotEmpty(state.WorldBirths);
        var birth=state.WorldBirths[0];
        Assert.InRange(Math.Abs(birth.World.Translation.X-birth.Time),0,.001f);
        Assert.Equal(new Vector3(definition.VelocityInheritanceFactor,0,0),birth.InheritedVelocityPerMillisecond);
        Advance(step*2,step,step);
        Assert.Equal(birth,state.WorldBirths[0]);
        uint end=step*2+(uint)Math.Ceiling(definition.LifetimeSeconds*1000)+1;
        Advance(end,end-step*2,step,false);
        Assert.Empty(state.WorldBirths);Assert.Empty(state.Births);Assert.Empty(state.Worlds);
    }

    [Fact]
    public void BoundaryBirthUsesFollowingEmissionFrameAndRetainsVelocity()
    {
        var history = new EnbCurrentRetailParticlePoseHistory();
        history.Record(0, Matrix4x4.Identity);
        history.Record(100, Matrix4x4.CreateTranslation(10,0,0));
        Assert.Equal(new Vector3(.1f,0,0), history.ResolveInheritedVelocity(99,1));
        Assert.Throws<InvalidDataException>(() => history.ResolveInheritedVelocity(100,1));
        history.Record(200, Matrix4x4.CreateTranslation(10,20,0));
        Assert.Equal(new Vector3(0,.2f,0), history.ResolveInheritedVelocity(100,1));
        Assert.Equal(new Vector3(0,.1f,0), history.ResolveInheritedVelocity(100,.5f));
        history.Record(300, Matrix4x4.CreateTranslation(1000,0,0));
        Assert.Equal(new Vector3(0,.2f,0), history.ResolveInheritedVelocity(100,1));
    }

    [Theory]
    [InlineData(511)]
    [InlineData(527)]
    [InlineData(536)]
    [InlineData(1501)]
    [InlineData(1502)]
    public void InstalledFiniteInheritanceAddsWorldVelocityWithoutScalingIt(int id)
    {
        if (EnbInstalledAssetLocator.TryLocate() is not { } paths) return;
        var loaded = EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths), id);
        var asset = Assert.Single(loaded.AggregateParticles!);
        Assert.NotEqual(0, asset.Definition.VelocityInheritanceFactor);
        var definition = asset.Definition.Copy().SetMaximumEmissions(1).SetBurstSize(1).SetEmissionRatePerSecond(10);
        var history = new EnbCurrentRetailParticlePoseHistory();
        history.Record(0, Matrix4x4.Identity);
        history.Record(100, Matrix4x4.CreateTranslation(10,0,0));
        history.Record(151, Matrix4x4.CreateTranslation(10,20,0));
        var emitter = new EnbW3dParticleEmitterDrawInstance(definition.Name,"fixture",1,
            EnbRenderPassKind.OrdinaryWorld,definition,asset.Texture,Matrix4x4.Identity,"",0,0);
        EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        Vector3[] Draw(bool moving)
        {
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,emitter,camera,
                EnbW3dLightEnvironment.BlackNoLights,1,0,151,out var packet,out var receipt,out var error,
                particleSizeScale:3, birthWorld:birth=>history.Resolve(birth,3),
                inheritedVelocity:birth=>moving ? history.ResolveInheritedVelocity(birth,definition.VelocityInheritanceFactor) : Vector3.Zero),error);
            Assert.Equal(1,receipt.LiveParticleCount);
            return Assert.IsType<EnbW3dDrawTask>(packet).DeformedGeometry!.Positions.ToArray();
        }
        var moving=Draw(true); var still=Draw(false);
        var displacement = new Vector3(0,20 * definition.VelocityInheritanceFactor,0);
        for(int i=0;i<moving.Length;i++) Assert.InRange(Vector3.Distance(moving[i]-still[i],displacement),0,.001f);
    }
}
