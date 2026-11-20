using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSpecializedMeshTextureTests
{
    [InstalledRetailTheory]
    [InlineData(false)] [InlineData(true)]
    public void PrimitiveMeshParentPreservesExplicitUnboundStages(bool sphere)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var source=assets.BaseAssetResolver!.Resolve(1877).W3d!;
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out var why),why);
        var unbound=new EnbW3dRenderTextureBinding(uint.MaxValue,string.Empty,null);
        resource=resource.Copy().SetMeshes(resource.Meshes.Select(m=>(m).Copy().SetMaterialPasses(m.MaterialPasses.Select(p=>(p).Copy().SetTextureStages(p.TextureStages.Select(s=>(s).Copy().SetTextures(s.Textures.Select(_=>unbound).ToArray()).SetCurrentRetailUniformTexture(unbound)).ToArray()).SetTriangleBindings(p.TriangleBindings.Select(t=>t with {Textures=t.Textures.Select(_=>(EnbW3dRenderTextureBinding?)unbound).ToArray()}).ToArray())).ToArray())).ToArray());
        var definition=source.CurrentRetailAggregate!.RenderDefinition;
        if(sphere)definition=definition with {Attachments=[..definition.Attachments,new("sk_clx01p2",definition.Attachments[0].BoneName)]};
        var prepared=sphere ? EnbRetailSphereAssembly.Load(assets,resource,definition).Resource! : EnbRetailRingAssembly.Load(assets,resource,definition).Resource!;
        var bindings=prepared.Meshes.SelectMany(m=>m.MaterialPasses).SelectMany(p=>p.TextureStages).SelectMany(s=>s.Textures).ToArray();
        Assert.NotEmpty(bindings);Assert.All(bindings,b=>Assert.True(b.IsUnbound));
    }

    [InstalledRetailTheory]
    [InlineData("ring",1877)] [InlineData("sphere",1877)]
    [InlineData("plume",1850)] [InlineData("tunnel",216)]
    public void NamedMissingMeshTextureUsesRetailImageWithoutDroppingChildren(string kind,int baseId)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var resolved=MeshTextureFailureFixture.Resolve(assets,assets.BaseAssetResolver!.Resolve(baseId));
        EnbW3dRenderResource resource;
        if(kind=="plume")
        {
            var asset=EnbWarpPlumeAsset.LoadAttachEffect(assets,710,Vector3.Zero,resolved);
            Assert.Single(asset.Particles);resource=asset.Resource;
        }
        else if(kind=="tunnel")
        {
            var asset=EnbWarpTunnelAsset.Load(assets,resolved);
            Assert.Equal(7,asset.Particles.Count);resource=asset.Resource;
        }
        else
        {
            if(kind=="sphere")
            {
                var source=resolved.W3d!;var aggregate=source.CurrentRetailAggregate!;
                var definition=aggregate.RenderDefinition with {Attachments=[..aggregate.RenderDefinition.Attachments,
                    new("sk_clx01p2",aggregate.RenderDefinition.Attachments[0].BoneName)]};
                resolved=resolved with {W3d=source with {CurrentRetailAggregate=new(definition,
                    aggregate.BaseLogicalFileName,aggregate.BaseRequestedObjectName,aggregate.BaseResourceLocation)}};
            }
            var asset=EnbCurrentRetailRObjEffectAsset.Load(assets,resolved);
            Assert.NotEmpty(asset.Rings!.Rings);
            if(kind=="sphere")Assert.Single(asset.Spheres!.Spheres);
            resource=asset.Resource!;
        }
        var fallback=assets.Textures.ResolveCurrentRetailMissingMeshTexture(MeshTextureFailureFixture.MissingName,assets.Paths.ClientExecutablePath);
        Assert.Equal(EnbTextureFormat.CurrentRetailMissingArgb8,fallback.Image!.Format);
        var bindings=resource.Meshes.SelectMany(m=>m.MaterialPasses).SelectMany(p=>p.TextureStages).SelectMany(s=>s.Textures).Where(t=>!t.IsUnbound).ToArray();
        Assert.NotEmpty(bindings);
        Assert.All(bindings,b=>Assert.Equal(fallback.LogicalName,b.LogicalName));
        Assert.All(resource.Meshes,m=>Assert.Equal(m.CurrentRetailLoadedTextureNames.Count,m.CurrentRetailLoadedTextureResources.Count));
    }
}
