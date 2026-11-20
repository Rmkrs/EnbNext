using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbStaticMixedLineTests
{
    [Fact]
    public void MissileTrailRetainsBirthsAndUsesCurrentEmitterOnlyWhileActive()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var loaded=EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1570);
        var asset=Assert.IsType<EnbCurrentRetailMixedEffectAsset>(loaded.Mixed);
        Assert.Null(asset.Animation);Assert.Equal(4,asset.Resource.Meshes.Count);
        var child=Assert.Single(asset.Sprites);Assert.Empty(asset.Streaks);
        Assert.Equal(2u,child.Definition.RenderMode);
        var runtime=new EnbCurrentRetailMixedParticleRuntime(asset,1,1,randomStreams:new());
        var history=new EnbCurrentRetailParticlePoseHistory();
        void Advance(uint time)
        {history.Record(time,Matrix4x4.CreateTranslation(time,0,0));runtime.Advance(asset,history,1);}
        Advance(0);Advance(11);Advance(21);
        var sprite=Assert.Single(runtime.Sprites);
        var continuous=Assert.IsType<EnbCurrentRetailShipParticleRuntime>(sprite.Continuous);
        Assert.Equal(new long[]{10,20},continuous.Births.Select(b=>b.Time));
        var births=continuous.Births.ToArray();Advance(21);
        Assert.Equal(births,continuous.Births);
        Assert.Null(runtime.AnimatedSprites);
        EnbResolvedRenderCamera.TryCreate(new(100,100,100),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        EnbW3dDrawTask Draw(bool active)
        {
            var instance=new EnbW3dParticleEmitterDrawInstance(child.Definition.Name,"fixture",1,
                EnbRenderPassKind.OrdinaryWorld,child.Definition,child.Texture,sprite.CurrentWorld,"",child.PivotIndex,0);
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(child.Key,1,1,instance,camera,
                EnbW3dLightEnvironment.BlackNoLights,1,0,21,out var packet,out _,out var error,
                worldBirths:continuous.Births,activeEmissionRun:active?continuous.EmissionRun:null),error);
            return Assert.IsType<EnbW3dDrawTask>(packet);
        }
        var active=Draw(true);var tail=Draw(false);
        Assert.Equal(tail.Mesh.Positions.Count+2,active.Mesh.Positions.Count);
        continuous.Advance(22,Matrix4x4.Identity,false);
        Assert.False(continuous.IsEmitting);Assert.Equal(births,continuous.Births);
        continuous.Advance(1300,Matrix4x4.Identity,false);Assert.Empty(continuous.Births);
    }
}
