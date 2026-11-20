using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbStandaloneParticleTextureTests
{
    [Fact]
    public void MissingParticleTextureRetainsNativeImageBindingInsteadOfBecomingUntextured()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        assets.PresentationCatalog!.LoadBaseAssetsText("[BASE-31000]\nFILE=we_blob01_p2.w3d\nNAME=we_blob01_p2\n");
        var loaded=EnbCurrentRetailRObjEffectAsset.LoadParticleJet(assets,31000);
        var original=loaded.Particle??Assert.Single(loaded.AggregateParticles!);
        const string missing="missing-particle-texture-653.tga";
        var particle=(original with {Definition=original.Definition.Copy().SetTextureFileName(missing)}).PrepareTexture(assets);
        Assert.Null(particle.Texture);
        Assert.NotNull(particle.RuntimeTextureName);
        Assert.Equal(EnbTextureResolutionStatus.MissingResource,assets.Textures.Resolve(missing).Status);
        Assert.Equal(EnbTextureFormat.CurrentRetailMissingArgb8,assets.Textures.Resolve(particle.RuntimeTextureName!).Image!.Format);
        Assert.Equal(missing,particle.Definition.TextureFileName);
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(100,100,100),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera));
        var instance=new EnbW3dParticleEmitterDrawInstance(particle.Definition.Name,particle.Definition.Name,1,
            EnbRenderPassKind.OrdinaryWorld,particle.Definition,particle.Texture,Matrix4x4.Identity,"",0,0)
            {RuntimeTextureName=particle.RuntimeTextureName};
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(particle.Key,1,1,instance,camera,
            EnbW3dLightEnvironment.BlackNoLights,1,0,100,out var packet,out _,out var error,
            worldBirths:[new(20,0,Matrix4x4.Identity),new(40,1,Matrix4x4.CreateTranslation(20,0,0))]),error);
        Assert.NotNull(packet);
        Assert.All(packet.Mesh.MaterialPasses,p=>Assert.NotEmpty(p.TextureStages));
        Assert.Null((particle with {Definition=particle.Definition.Copy().SetTextureFileName("")}).PrepareTexture(assets).RuntimeTextureName);
    }

    [Fact]
    public void AuthoredEmptyTextureKeepsStandaloneParticleUnbound()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        assets.PresentationCatalog!.LoadBaseAssetsText("[BASE-31000]\nFILE=we_blob01_p2.w3d\nNAME=we_blob01_p2\n");
        var asset=EnbCurrentRetailRObjEffectAsset.LoadParticleJet(assets,31000);
        var particle=asset.Particle??Assert.Single(asset.AggregateParticles!);
        Assert.Empty(particle.Definition.TextureFileName);Assert.Null(particle.Texture);
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(100,100,100),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera));
        var instance=new EnbW3dParticleEmitterDrawInstance(particle.Definition.Name,particle.Definition.Name,1,
            EnbRenderPassKind.OrdinaryWorld,particle.Definition,particle.Texture,Matrix4x4.Identity,"",0,0);
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(particle.Key,1,1,instance,camera,
            EnbW3dLightEnvironment.BlackNoLights,1,0,100,out var packet,out var receipt,out var error,
            worldBirths:[new(20,0,Matrix4x4.Identity),new(40,1,Matrix4x4.CreateTranslation(20,0,0))]),error);
        Assert.True(receipt.LiveParticleCount>0);Assert.NotNull(packet);
        Assert.All(packet.Mesh.MaterialPasses,p=>Assert.Empty(p.TextureStages));
    }
}
