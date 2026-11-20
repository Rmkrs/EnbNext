using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10TexturedStreakTests
{
    [Fact]
    public void MissingStargateTextureUsesRuntimeImageWithoutChangingAuthoredNameOrHsvImage()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var original=EnbCurrentRetailRObjEffectAsset.Load(assets,62).Mixed!;
        var child=Assert.Single(original.Streaks);
        Assert.Equal("cloudy.tga",child.Definition.TextureFileName,ignoreCase:true);
        Assert.Null(child.Texture);
        Assert.NotNull(child.RuntimeTextureName);
        Assert.Equal(EnbTextureResolutionStatus.MissingResource,assets.Textures.Resolve("cloudy.tga").Status);
        var image=assets.Textures.Resolve(child.RuntimeTextureName!).Image;
        Assert.Equal(EnbTextureFormat.CurrentRetailMissingArgb8,image!.Format);
        var shifted=Assert.Single(EnbCurrentRetailRObjEffectAsset.Load(assets,62,new(-60,0,0)).Mixed!.Streaks);
        Assert.Equal(child.RuntimeTextureName,shifted.RuntimeTextureName);
        // The HSV clone retains the resolved failed-load image under its
        // runtime key; it does not restart acquisition of the missing file.
        Assert.Equal(child.RuntimeTextureName,shifted.Definition.TextureFileName);
        Assert.Equal("cloudy.tga",child.Definition.TextureFileName,ignoreCase:true);
        Assert.Same(image,assets.Textures.Resolve(shifted.RuntimeTextureName!).Image);
        EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        var sample=new EnbCurrentRetailStreakSample(new(100,0,0),new(90,0,0),2,new(255,255,255,255),new(0,0,0,255));
        Assert.True(EnbCurrentRetailStreakRuntime.TryBuildPacket(child.Key,1,1,1,child.Definition.Name,0,
            child.Definition,[sample],camera,100,out var packet,out var error,child.Texture,child.RuntimeTextureName),error);
        var binding=Assert.Single(Assert.Single(packet!.Mesh.MaterialPasses).TextureStages).Textures.Single();
        Assert.True(binding.ResolveLogicalNameAtRuntime);
        Assert.Equal(child.RuntimeTextureName,binding.LogicalName);
    }

    [Fact]
    public void StargateUsesContinuousFrameCoordinatesAndIgnoresSpriteOrientation()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        Assert.True(assets.RetailMixResources!.TryReadResource("nsg01p1.w3d",out var bytes,out var location));
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes,out var definition,out var diagnostic),diagnostic);
        // Geometry-only receipt uses an explicit fixture location; production
        // missing-image admission is covered independently below.
        var child=new EnbCurrentRetailStreakAttachment(EnbW3dRenderResourceKey.From(location),definition,0)
            {Texture=new("cloudy.tga","fixture.mix",0,0)};
        Assert.Equal(1,child.Definition.InitialOrientationRandom);
        var buffer=new EnbCurrentRetailStreakBufferRuntime(child.Definition,1,42,randomStreams:new());
        var noOrientation=new EnbCurrentRetailStreakBufferRuntime(child.Definition.Copy().SetInitialOrientationRandom(0),1,42,randomStreams:new());
        foreach(var runtime in new[]{buffer,noOrientation})
        {runtime.Advance(0,Matrix4x4.Identity);runtime.Advance(501,Matrix4x4.Identity);}
        var samples=buffer.Sample(501);
        Assert.Equal(samples,noOrientation.Sample(501));
        Assert.NotEmpty(samples);
        Assert.All(samples,s=>{Assert.InRange(s.Frame,1.0001f,1.2f);Assert.InRange(s.Size,10,30);});
        Assert.True(samples.Select(s=>s.Size).Distinct().Count()>1);
        EnbResolvedRenderCamera.TryCreate(new(200,0,0),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        Assert.True(EnbCurrentRetailStreakRuntime.TryBuildPacket(child.Key,1,1,1,child.Definition.Name,0,
            child.Definition,samples,camera,501,out var packet,out var error,child.Texture),error);
        var uv=packet!.Mesh.MaterialPasses.Single().TextureStages.Single().EffectiveRuntimeTexCoords!;
        for(int i=0;i<samples.Count;i++)
            for(int v=0;v<4;v++) Assert.Equal(new Vector2(samples[i].Frame,v==0 ? 1 : 0),uv[i*4+v]);
    }

    [Fact]
    public void InstalledEnergyStealRetainsTextureAndEqualEndpointColours()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var mixed=EnbCurrentRetailRObjEffectAsset.Load(assets,1763).Mixed!;
        var child=Assert.Single(mixed.Streaks);
        Assert.Single(mixed.Sprites);
        Assert.NotNull(child.Texture);
        Assert.Equal("ge_en_steal01.tga",child.Definition.TextureFileName,StringComparer.OrdinalIgnoreCase);
        var buffer=new EnbCurrentRetailStreakBufferRuntime(child.Definition,100,42,randomStreams:new());
        buffer.Advance(0,Matrix4x4.Identity);
        buffer.Advance(200,Matrix4x4.Identity);
        var samples=buffer.Sample(200);
        Assert.NotEmpty(samples);
        Assert.All(samples,s=>Assert.Equal(s.HeadColor,s.TailColor));
        Assert.Contains(samples,s=>s.HeadColor.R!=0 || s.HeadColor.G!=0 || s.HeadColor.B!=0);
        EnbResolvedRenderCamera.TryCreate(new(200,0,0),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        Assert.True(EnbCurrentRetailStreakRuntime.TryBuildPacket(child.Key,1,1,1,child.Definition.Name,0,
            child.Definition,samples,camera,200,out var packet,out var error,child.Texture),error);
        var stage=Assert.Single(packet!.Mesh.MaterialPasses.Single().TextureStages);
        Assert.Equal(child.Definition.TextureFileName,Assert.Single(stage.Textures).LogicalName);
        var uv=stage.EffectiveRuntimeTexCoords!;
        for(int i=0;i<samples.Count;i++)
            for(int corner=0;corner<6;corner++)Assert.Equal(new Vector2(0,corner<3 ? 0 : 1),uv[i*6+corner]);
        Assert.True(EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out error),error);
        Assert.True(EnbW3dProgrammableTextureLaneCompiler.TryCompile(submission,camera,
            EnbRenderTextureRuntimeSettings.RetailDefaults,EnbRenderTextureBackendCapabilities.ModernDefault,
            binding=>assets.Textures.Resolve(binding.LogicalName).Image,out var lane,out error),error);
        Assert.All(lane.RunStates,r=>Assert.True(r.SamplesTexture0));
        buffer.Advance(200,Matrix4x4.CreateTranslation(500,0,0),false);
        Assert.NotEmpty(buffer.Sample(220));
        Assert.Empty(buffer.Sample(500));
    }

    [Fact]
    public void TexturedStreakHsvUsesVariantWithoutMutatingAuthoredMaterial()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var original=EnbCurrentRetailRObjEffectAsset.Load(assets,1763).Mixed!;
        var shifted=EnbCurrentRetailRObjEffectAsset.Load(assets,1763,new(120,-.1f,-.1f)).Mixed!;
        var a=Assert.Single(original.Streaks);var b=Assert.Single(shifted.Streaks);
        Assert.Equal(a.Texture,b.Texture);
        Assert.NotEqual(a.Definition.TextureFileName,b.Definition.TextureFileName);
        Assert.NotNull(assets.Textures.Resolve(b.Definition.TextureFileName).Image);
        Assert.Equal("ge_en_steal01.tga",a.Definition.TextureFileName,StringComparer.OrdinalIgnoreCase);
        Assert.Equal(a.PivotIndex,b.PivotIndex);
    }
}
