using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbParticleStaticInputTests
{
    [InstalledRetailTheory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void ZeroFrameAtlasIsRetainedWhileGeometryChangesAndQueuedViewsSurviveResize(uint mode)
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        var asset=Assert.Single(EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1188).AggregateParticles!);
        var definition=asset.Definition.Copy().SetRenderMode(mode).SetFrameMode(0).SetMaximumEmissions(64).SetBurstSize(1).SetLifetimeSeconds(10).SetFrame(new(5,[new(0,3),new(1,7)])).SetCreationRandomizer(new(0,Vector3.Zero)).SetVelocityRandomizer(new(0,Vector3.Zero)).SetVelocityPerSecond(new(1,0,0)).SetOutwardVelocityPerSecond(0).SetAccelerationPerSecondSquared(Vector3.Zero);
        EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        EnbW3dDrawTask Build(int count,long time)
        {
            var instance=new EnbW3dParticleEmitterDrawInstance(definition.Name,"fixture",1,EnbRenderPassKind.OrdinaryWorld,
                definition,asset.Texture,Matrix4x4.Identity,"",0,0);
            var births=Enumerable.Range(0,count).Select(i=>new EnbCurrentRetailParticleBirth(0,i,Matrix4x4.Identity)).ToArray();
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,instance,camera,
                EnbW3dLightEnvironment.BlackNoLights,0,0,time,out var packet,out _,out var error,worldBirths:births),error);
            return Assert.IsType<EnbW3dDrawTask>(packet);
        }
        static ImmutableArray<Vector2> Uv(EnbW3dDrawTask packet)=>
            Assert.IsType<ImmutableArray<Vector2>>(packet.Mesh.MaterialPasses[0].TextureStages[0].EffectiveRuntimeTexCoords);
        var first=Build(2,100);var firstPositions=first.Mesh.Positions.ToArray();var uv=Uv(first);
        var moved=Build(2,500);Assert.True(uv.Equals(Uv(moved)));
        Assert.False(first.Mesh.Positions.SequenceEqual(moved.Mesh.Positions));
        var grown=Build(7,500);var smaller=Build(1,500);
        Assert.Equal(2*(mode==1?4:3),uv.Length);
        Assert.Equal(7*(mode==1?4:3),Uv(grown).Length);
        Assert.Equal(mode==1?4:3,Uv(smaller).Length);
        Assert.Equal(firstPositions,first.Mesh.Positions);
        Vector2[] expected=mode==1?[new(0,0),new(0,1),new(1,1),new(1,0)]:[new(.5f,0),new(0,.866f),new(1,.866f)];
        for(int i=0;i<uv.Length;i++)Assert.Equal(expected[i%expected.Length],uv[i]);
    }
}
