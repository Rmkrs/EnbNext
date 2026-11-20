using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbParticleRenderOwnerTests
{
    private sealed class Owner(EnbCurrentRetailParticleEmitterDefinition definition,
        EnbW3dRenderTextureLocation? texture) : IEnbParticleRenderOwner
    {
        public string RenderObjectName=>Definition.Name;
        public string EmitterResourceName=>"owner-fixture";
        public uint GameId=>17;
        public EnbRenderPassKind RenderPass=>EnbRenderPassKind.OrdinaryWorld;
        public EnbCurrentRetailParticleEmitterDefinition Definition {get;}=definition;
        public EnbW3dRenderTextureLocation? TextureLocation {get;}=texture;
        public Matrix4x4 WorldTransform {get;set;}=Matrix4x4.Identity;
        public string HierarchyName=>"";
        public int PivotIndex=>3;
        public int BindingOrdinal=>7;
        public string? RuntimeTextureName=>null;
        public EnbResolvedTextureResource? RetainedTexture=>null;
        public EnbW3dParticleEmitterDrawInstance Capture()=>new(RenderObjectName,EmitterResourceName,GameId,
            RenderPass,Definition,TextureLocation,WorldTransform,HierarchyName,PivotIndex,BindingOrdinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenderAndBoundsReadCurrentOwnerWithoutChangingQueuedValues(bool local)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var asset=Assert.Single(EnbCurrentRetailRObjEffectAsset.Load(EnbRetailAssetBundle.Load(paths),1188).AggregateParticles!);
        var definition=asset.Definition.Copy().SetMaximumEmissions(1).SetBurstSize(1).SetLifetimeSeconds(1).SetLocalSpace(local);
        var owner=new Owner(definition,asset.Texture);
        EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        EnbCurrentRetailParticleBirth[] births=[new(0,0,Matrix4x4.Identity)];
        var pending=new List<(EnbW3dDrawTask Task,Vector3[] Positions,Matrix4x4 World)>();
        foreach(var translation in new[]{new Vector3(0,0,100),new Vector3(10,20,100),new Vector3(-20,5,100)})
        {
            owner.WorldTransform=Matrix4x4.CreateTranslation(translation);
            var capture=owner.Capture();
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,owner,camera,
                EnbW3dLightEnvironment.BlackNoLights,0,0,100,out var actual,out var receipt,out var error,worldBirths:births),error);
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,1,1,capture,camera,
                EnbW3dLightEnvironment.BlackNoLights,0,0,100,out var expected,out var reference,out error,worldBirths:births),error);
            Assert.NotNull(actual);Assert.NotNull(expected);
            Assert.Equal(reference.LiveParticleCount,receipt.LiveParticleCount);
            Assert.Equal(expected.WorldTransform,actual.WorldTransform);
            Assert.Equal(expected.Bounds,actual.Bounds);
            Assert.Equal(expected.Mesh.Positions.ToArray(),actual.Mesh.Positions.ToArray());
            Assert.Equal(expected.Mesh.Indices.ToArray(),actual.Mesh.Indices.ToArray());
            Assert.Equal(expected.Mesh.EffectiveColor1.ToArray(),actual.Mesh.EffectiveColor1.ToArray());
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryComputeBufferBounds(owner,births,0,100,1,new(),1,out var bounds,out error),error);
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryComputeBufferBounds(capture,births,0,100,1,new(),1,out var oldBounds,out error),error);
            Assert.Equal(oldBounds,bounds);
            pending.Add((actual,actual.Mesh.Positions.ToArray(),actual.WorldTransform));
            foreach(var held in pending){Assert.Equal(held.World,held.Task.WorldTransform);Assert.Equal(held.Positions,held.Task.Mesh.Positions.ToArray());}
        }
    }
}
