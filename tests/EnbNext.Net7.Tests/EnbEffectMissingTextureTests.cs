using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbEffectMissingTextureTests
{
    [Fact]
    public void UnusedLoadedMissingTextureRemainsPreparedAcrossHsvAndLaterNamedPublication()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var installed=EnbCurrentRetailRObjEffectAsset.Load(assets,1063).Resource!;
        const string missing="MISSILE.TGA";
        var source=installed.Copy().SetMeshes(installed.Meshes.Select(mesh=>(mesh).Copy().SetCurrentRetailLoadedTextureNames(mesh.CurrentRetailLoadedTextureNames.Append(missing).ToArray()).SetCurrentRetailLoadedTextureResources([])).ToArray());
        Assert.DoesNotContain(source.Meshes.SelectMany(mesh=>mesh.MaterialPasses)
            .SelectMany(pass=>pass.TextureStages).SelectMany(stage=>stage.Textures),binding=>binding.LogicalName==missing);
        var prepared=EnbCurrentRetailMeshTexturePreparation.Prepare(source,assets);
        Assert.Equal(source.Key,prepared.Key);
        Assert.All(source.Meshes,mesh=>Assert.Empty(mesh.CurrentRetailLoadedTextureResources));
        var fallback=assets.Textures.ResolveCurrentRetailMissingMeshTexture(missing,assets.Paths.ClientExecutablePath);
        Assert.All(prepared.Meshes,mesh=>Assert.Same(fallback,mesh.CurrentRetailLoadedTextureResources[^1]));
        var recolored=EnbCurrentRetailEffectMeshHsvRuntime.Apply(prepared,assets.Textures,new(120,0,0));
        Assert.All(recolored.Meshes,mesh=>Assert.Same(fallback,mesh.CurrentRetailLoadedTextureResources[^1]));
        var retained=recolored.Meshes.SelectMany(mesh=>mesh.CurrentRetailLoadedTextureResources)
            .First(texture=>texture.CurrentRetailOriginalTextureName is not null);
        var published=assets.Textures.RecolorCurrentRetailTexture(retained.CurrentRetailOriginalTextureName!,new(120,0,0));
        Assert.NotSame(retained,published);
        Assert.Same(published,assets.Textures.Resolve(retained.LogicalName));
        Assert.Same(recolored,EnbCurrentRetailMeshTexturePreparation.Prepare(recolored,assets));
        Assert.Contains(recolored.Meshes.SelectMany(mesh=>mesh.CurrentRetailLoadedTextureResources),texture=>ReferenceEquals(texture,retained));
    }

    [Theory]
    [InlineData(228, "MISSILE.TGA")]
    [InlineData(0, "rvc02_detail02.tga")]
    public void NamedMissingMeshTextureKeepsSourceFailureAndUnchangedFallbackUnderHsv(int id,string missing)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        EnbW3dRenderResource Load(Vector3 hsv)=> (id==0 ? EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,"RVC02B",hsv) : EnbCurrentRetailRObjEffectAsset.Load(assets,id,hsv)).Resource!;
        var original=Load(Vector3.Zero);var shifted=Load(new(-60,0,0));
        var source=assets.Textures.Resolve(missing);
        Assert.Equal(EnbTextureResolutionStatus.MissingResource,source.Status);Assert.Null(source.Image);
        //0048CB40 publishes the recolored object; its failed load retains 009AF0F0.
        var recolored=assets.Textures.ResolveCurrentRetailHsv(missing,new(-60,0,0));
        Assert.Equal(EnbTextureFormat.CurrentRetailMissingArgb8,recolored.Image!.Format);
        Assert.Equal(missing,recolored.CurrentRetailOriginalTextureName);
        Assert.Same(recolored,assets.Textures.Resolve(recolored.LogicalName));
        var fallback=assets.Textures.ResolveCurrentRetailMissingMeshTexture(missing,assets.Paths.ClientExecutablePath);
        Assert.Contains(missing,fallback.Diagnostic!,StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EnbTextureFormat.CurrentRetailMissingArgb8,fallback.Image!.Format);
        var expected=EnbRetailMissingTexture.Load(assets.Paths.ClientExecutablePath);
        Assert.Equal(expected.MipLevels.Count,fallback.Image.MipLevels.Count);
        foreach(var (a,b) in expected.MipLevels.Zip(fallback.Image.MipLevels))Assert.Equal(a.Pixels.ToArray(),b.Pixels.ToArray());
        foreach(var resource in new[]{original,shifted})
        {
            var stages=resource.Meshes.SelectMany(m=>m.MaterialPasses).SelectMany(p=>p.TextureStages).SelectMany(s=>s.Textures);
            var triangles=resource.Meshes.SelectMany(m=>m.MaterialPasses).SelectMany(p=>p.TriangleBindings).SelectMany(t=>t.Textures).OfType<EnbW3dRenderTextureBinding>();
            foreach(var bindings in new[]{stages,triangles})
            {
                var uses=bindings.Where(b=>b.LogicalName==fallback.LogicalName).ToArray();Assert.NotEmpty(uses);
                Assert.All(uses,b=>{Assert.True(b.ResolveLogicalNameAtRuntime);Assert.False(b.IsMissing);Assert.False(b.IsUnbound);Assert.Null(b.Location);});
            }
        }
        Assert.Same(fallback,assets.Textures.Resolve(fallback.LogicalName));
        Assert.Same(fallback,assets.Textures.ResolveCurrentRetailHsv(fallback.LogicalName,new(-60,0,0)));
        Assert.Equal(EnbTextureResolutionStatus.MissingResource,assets.Textures.Resolve(missing).Status);
        string nonTexture=assets.BaseAssetResolver!.Resolve(228).W3d!.ResourceLocation.Name;
        var unsupported=assets.Textures.Resolve(nonTexture);
        Assert.Equal(EnbTextureResolutionStatus.UnsupportedOrInvalidTexture,unsupported.Status);
        Assert.Same(unsupported,assets.Textures.ResolveCurrentRetailMissingMeshTexture(nonTexture,assets.Paths.ClientExecutablePath));
    }

    [Fact]
    public void OrdinaryMissingAdmissionPreservesUnsupportedDecoderAndExplicitUnboundStages()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var source=EnbCurrentRetailRObjEffectAsset.Load(assets,1063).Resource!;
        string nonTexture=assets.BaseAssetResolver!.Resolve(228).W3d!.ResourceLocation.Name;
        Assert.Equal(EnbTextureResolutionStatus.UnsupportedOrInvalidTexture,assets.Textures.Resolve(nonTexture).Status);
        var original=source.Meshes.First(mesh=>mesh.MaterialPasses.Any(pass=>pass.TextureStages.Count>0));
        var pass=original.MaterialPasses.First(value=>value.TextureStages.Count>0);
        var invalid=new EnbW3dRenderTextureBinding(0,nonTexture,null);
        var unbound=new EnbW3dRenderTextureBinding(uint.MaxValue,"absent-unbound.tga",null);
        var stage=(pass.TextureStages[0]).Copy().SetTextures([invalid,unbound]).SetCurrentRetailUniformTexture(invalid);
        var fixture=source.Copy().SetMeshes([(original).Copy().SetMaterialPasses([(pass).Copy().SetTextureStages([stage]).SetTriangleBindings([])])]);
        var result=EnbCurrentRetailMeshTexturePreparation.Prepare(fixture,assets,missingResourcesOnly:true);
        Assert.Same(invalid,result.Meshes[0].MaterialPasses[0].TextureStages[0].Textures[0]);
        Assert.Same(unbound,result.Meshes[0].MaterialPasses[0].TextureStages[0].Textures[1]);
        Assert.True(invalid.IsMissing);
        Assert.True(unbound.IsUnbound);
    }
}
