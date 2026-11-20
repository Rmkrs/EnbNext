using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbOrdinaryAggregateLineParticleTests
{
    [Theory]
    [InlineData("weap_am03p1")]
    [InlineData("weap_am03p2")]
    public void InstalledRejectedOrdinaryLinesRequireRetainedBirths(string name)
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var assets=EnbRetailAssetBundle.Load(paths);
        Assert.True(assets.RetailMixResources!.TryReadResource(name+".w3d",out var bytes,out var location));
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes,out var definition,out var error),error);
        Assert.True(assets.RetailMixResources.TryLocateResource(definition.TextureFileName,out var texture));
        Assert.NotNull(assets.Textures.Resolve(definition.TextureFileName).Image);
        Assert.Equal(2u,definition.RenderMode);
        Assert.Equal(0,definition.MaximumEmissions);
        var runtime=new EnbCurrentRetailShipParticleRuntime(definition,allowInheritedMotion:true);
        for(int time=0;time<=1000;time+=10)
            runtime.Advance(time,Matrix4x4.CreateTranslation(time*.1f,0,100),true);
        var emitter=new EnbW3dParticleEmitterDrawInstance(name,name+".w3d",1,EnbRenderPassKind.OrdinaryWorld,
            definition,new(texture.Name,texture.ArchivePath,texture.DataOffset,texture.DataLength),runtime.CurrentWorld,"fixture",0,0);
        Assert.True(EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera));
        var key=EnbW3dRenderResourceKey.From(location);
        // This is the current ordinary caller's boundary, reproduced without GPU.
        Assert.False(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(key,1,1,emitter,camera,
            EnbW3dLightEnvironment.BlackNoLights,1,0,1000,out _,out _,out _));
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(key,1,1,emitter,camera,
            EnbW3dLightEnvironment.BlackNoLights,1,0,1000,out var packet,out var receipt,out error,
            worldBirths:runtime.Births,activeEmissionRun:runtime.EmissionRun),error);
        Assert.NotNull(packet);
        Assert.True(receipt.LiveParticleCount>0);
        Assert.Contains(runtime.Births,b=>b.World.Translation.X<runtime.CurrentWorld.Translation.X);
        long count=runtime.EmittedCount;
        runtime.Advance(1000,runtime.CurrentWorld,true); // Same-time second view cannot emit again.
        Assert.Equal(count,runtime.EmittedCount);
        runtime.Advance(1010,runtime.CurrentWorld,false);
        // Emitter destruction unlinks the separate buffer (0097D820 ->
        // 00960CF0); its completion predicate 0095C310 waits for empty counts.
        // A detached mode-2 buffer must still build geometry without a nozzle.
        Assert.False(runtime.IsEmitting);
        Assert.NotEmpty(runtime.Births);
        Assert.All(runtime.Births,b=>Assert.True(b.Time<=1000));
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(key,1,1,emitter,camera,
            EnbW3dLightEnvironment.BlackNoLights,1,0,1010,out var tail,out var tailReceipt,out error,
            worldBirths:runtime.Births,activeEmissionRun:null),error);
        Assert.NotNull(tail);
        Assert.True(tailReceipt.LiveParticleCount>0);
        runtime.Advance(2000,runtime.CurrentWorld,false);
        Assert.Equal(count,runtime.EmittedCount);
        Assert.Empty(runtime.Births);
    }
}
