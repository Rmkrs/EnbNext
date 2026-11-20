using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetainedParticleTextureTests
{
    [Fact]
    public void LinePacketRetainsItsBufferTextureAfterRegistryReplacement()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var sprite=EnbCurrentRetailRObjEffectAsset.Load(assets,assets.PresentationCatalog!.FindEffect(311)!.BaseAssetId!.Value);
        string name=(sprite.Particle??Assert.Single(sprite.AggregateParticles!)).Definition.TextureFileName;
        assets.PresentationCatalog.LoadBaseAssetsText("[BASE-31000]\nFILE=we_blob01_p2.w3d\nNAME=we_blob01_p2\n");
        var loaded=EnbCurrentRetailRObjEffectAsset.LoadParticleJet(assets,31000);
        var original=loaded.Particle??Assert.Single(loaded.AggregateParticles!);
        var shift=new Vector3(.5f,.005f,.005f);
        var particle=(original with {Definition=original.Definition.Copy().SetTextureFileName(name)}).PrepareTexture(assets).ApplyHsv(assets,shift);
        assets.Textures.RecolorCurrentRetailTexture(name,shift);
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(100,100,100),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera));
        var instance=new EnbW3dParticleEmitterDrawInstance(particle.Definition.Name,particle.Definition.Name,1,
            EnbRenderPassKind.OrdinaryWorld,particle.Definition,particle.Texture,Matrix4x4.Identity,"",0,0)
            {RuntimeTextureName=particle.RuntimeTextureName,RetainedTexture=particle.RetainedTexture};
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(particle.Key,1,1,instance,camera,
            EnbW3dLightEnvironment.BlackNoLights,1,0,100,out var packet,out _,out var error,
            worldBirths:[new(20,0,Matrix4x4.Identity),new(40,1,Matrix4x4.CreateTranslation(20,0,0))]),error);
        Assert.NotNull(packet);
        var bindings=packet.Mesh.MaterialPasses.SelectMany(p=>p.TextureStages).SelectMany(s=>s.Textures).ToArray();
        Assert.NotEmpty(bindings);
        Assert.All(bindings,b=>Assert.Same(particle.RetainedTexture,b.CurrentRetailResolvedTexture));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RecolorAndDrawRetainTheAcquiredParticleTexture(bool streak)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        int selector=assets.PresentationCatalog!.FindEffect(311)!.BaseAssetId!.Value;
        var loaded=EnbCurrentRetailRObjEffectAsset.Load(assets,selector);
        var original=loaded.Particle??Assert.Single(loaded.AggregateParticles!);
        if(streak)
        {
            var standalone=EnbCurrentRetailRObjEffectAsset.Load(assets,1583).StandaloneStreak!;
            original=new EnbCurrentRetailRObjParticleAsset(standalone.Key,null,standalone.Definition.Copy().SetTextureFileName(original.Definition.TextureFileName)).PrepareTexture(assets);
        }
        var source=Assert.IsType<EnbResolvedTextureResource>(original.RetainedTexture);
        var shift=new Vector3(.5f,.005f,.005f);
        Assert.False(EnbCurrentRetailAssetVariantName.ChangesHsv(shift));
        var first=original.ApplyHsv(assets,shift);
        var retained=Assert.IsType<EnbResolvedTextureResource>(first.RetainedTexture);
        Assert.Equal(EnbCurrentRetailAssetVariantName.CreateTexture(source.LogicalName,shift),retained.LogicalName);
        Assert.NotSame(source.Image,retained.Image);
        var newest=assets.Textures.RecolorCurrentRetailTexture(source.LogicalName,shift);
        Assert.Same(newest,original.ApplyHsv(assets,shift).RetainedTexture);
        Assert.NotSame(newest,first.RetainedTexture);
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(10,0,0),Quaternion.Identity,new(800,600,1),Math.PI/2,new(.01f,10000),out var camera));
        EnbW3dDrawTask? packet;
        string? error;
        if(streak)
        {
            var attachment=new EnbCurrentRetailStreakAttachment(original.Key,original.Definition,0)
                {Texture=original.Texture,RetainedTexture=source};
            var shifted=attachment.ApplyHsv(assets,shift);
            Assert.Same(newest,shifted.RetainedTexture);
            Assert.True(EnbCurrentRetailStreakRuntime.TryBuildPacket(first.Key,1,1,1,"retained",0,first.Definition,
                [new(Vector3.UnitX,Vector3.Zero,1,new(255,255,255,255),new(255,255,255,255))],camera,100,
                out packet,out error,first.Texture,first.RuntimeTextureName,first.RetainedTexture),error);
        }
        else
        {
            var instance=new EnbW3dParticleEmitterDrawInstance(first.Definition.Name,first.Key.ResourceName,1,
                EnbRenderPassKind.OrdinaryWorld,first.Definition,first.Texture,Matrix4x4.Identity,"",0,0)
                {RuntimeTextureName=first.RuntimeTextureName,RetainedTexture=first.RetainedTexture};
            Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(first.Key,1,1,instance,camera,
                EnbW3dLightEnvironment.BlackNoLights,1,0,500,out packet,out _,out error),error);
        }
        Assert.NotNull(packet);
        var bindings=packet.Mesh.MaterialPasses.SelectMany(p=>p.TextureStages).SelectMany(s=>s.Textures).ToArray();
        Assert.NotEmpty(bindings);
        Assert.All(bindings,b=>Assert.Same(retained,b.CurrentRetailResolvedTexture));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MissingAndEmptyParticleTexturesSurviveRecolor(bool empty)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var streak=EnbCurrentRetailRObjEffectAsset.Load(assets,1583).StandaloneStreak!;
        var definition=streak.Definition.Copy().SetTextureFileName(empty?"":"missing-particle-retained-674.tga");
        var sprite=new EnbCurrentRetailRObjParticleAsset(streak.Key,null,definition).PrepareTexture(assets);
        var line=new EnbCurrentRetailStreakAttachment(streak.Key,definition,0).PrepareTexture(assets);
        var cleared=(line with {Definition=definition.Copy().SetTextureFileName("")}).PrepareTexture(assets);
        Assert.Null(cleared.Texture);Assert.Null(cleared.RuntimeTextureName);Assert.Null(cleared.RetainedTexture);
        foreach(var pair in new[]{(sprite.RetainedTexture,sprite.ApplyHsv(assets,new(120,.1f,0)).RetainedTexture),
            (line.RetainedTexture,line.ApplyHsv(assets,new(120,.1f,0)).RetainedTexture)})
        {
            if(empty){Assert.Null(pair.Item1);Assert.Null(pair.Item2);}
            else {Assert.Equal(EnbTextureFormat.CurrentRetailMissingArgb8,pair.Item1!.Image!.Format);Assert.Same(pair.Item1,pair.Item2);}
        }
    }
}
