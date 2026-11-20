using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbStandalonePrimitiveTextureTests
{
    [InstalledRetailTheory]
    [InlineData(false)] [InlineData(true)]
    public void PrimitiveRecolorUsesTheDirectRetainedTexturePath(bool sphere)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        if(sphere)assets.PresentationCatalog!.LoadBaseAssetsText("[BASE-31000]\nFILE=sk_clx01p2.w3d\nNAME=sk_clx01p2\n");
        int selector=sphere?31000:1196;
        var original=EnbCurrentRetailRObjEffectAsset.Load(assets,selector);
        EnbW3dRenderTextureBinding Binding(EnbCurrentRetailRObjEffectAsset value)=>sphere
            ?Assert.Single(value.Spheres!.Spheres).Texture!:Assert.Single(value.Rings!.Rings).Texture!;
        var source=Binding(original).CurrentRetailResolvedTexture!;
        var shift=new Vector3(.5f,.005f,.005f);
        Assert.False(EnbCurrentRetailAssetVariantName.ChangesHsv(shift));
        var first=Binding(EnbCurrentRetailRObjEffectAsset.Load(assets,selector,shift));
        Assert.Equal(EnbCurrentRetailAssetVariantName.CreateTexture(source.LogicalName,shift),first.LogicalName);
        Assert.NotSame(source.Image,first.CurrentRetailResolvedTexture!.Image);
        //0048CF90 returns the newest matching publication before constructing.
        var replacement=assets.Textures.RecolorCurrentRetailTexture(source.LogicalName,shift);
        var second=Binding(EnbCurrentRetailRObjEffectAsset.Load(assets,selector,shift));
        Assert.Same(replacement,second.CurrentRetailResolvedTexture);
        Assert.NotSame(first.CurrentRetailResolvedTexture,second.CurrentRetailResolvedTexture);
        Assert.NotSame(source.Image,second.CurrentRetailResolvedTexture!.Image);
    }

    [InstalledRetailTheory]
    [InlineData(false,false)] [InlineData(false,true)]
    [InlineData(true,false)] [InlineData(true,true)]
    public void NamedFailureRetainsNativeImageWhileEmptyNameStaysUnbound(bool sphere,bool empty)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var definition=assets.PresentationCatalog!.ResolveBaseAsset(1196);
        string name=sphere?"sk_clx01p2":definition.Name!;
        Assert.True(assets.FileResources.TryReadResource(sphere?name+".w3d":definition.File!,out var raw,out _));
        Assert.Equal(1u,BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8)));
        //A unique NAME forces the authored FILE path instead of ordinary
        //NAME-first acquisition of the unmodified installed prototype.
        name="PRIMTEX739";raw.AsSpan(24,32).Clear();Encoding.ASCII.GetBytes(name).CopyTo(raw.AsSpan(24));
        const string missing="missing-primitive-671.tga";
        raw.AsSpan(16+0x74,32).Clear();
        if(!empty)Encoding.ASCII.GetBytes(missing).CopyTo(raw.AsSpan(16+0x74));
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-PrimitiveTexture-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file=Path.Combine(directory,"primitive.w3d");File.WriteAllBytes(file,raw);
            assets.PresentationCatalog.LoadBaseAssetsText($"[BASE-31000]\nFILE={file}\nNAME={name}\n");
            var plain=EnbCurrentRetailRObjEffectAsset.Load(assets,31000);
            var shifted=EnbCurrentRetailRObjEffectAsset.Load(assets,31000,new Vector3(120,.1f,0));
            EnbW3dRenderTextureBinding? Binding(EnbCurrentRetailRObjEffectAsset value)=>sphere
                ?Assert.Single(value.Spheres!.Spheres).Texture:Assert.Single(value.Rings!.Rings).Texture;
            if(empty){Assert.Null(Binding(plain));Assert.Null(Binding(shifted));return;}
            var binding=Assert.IsType<EnbW3dRenderTextureBinding>(Binding(plain));
            Assert.True(binding.ResolveLogicalNameAtRuntime);Assert.False(binding.IsMissing);Assert.False(binding.IsUnbound);
            Assert.Null(binding.Location);
            var image=binding.CurrentRetailResolvedTexture!.Image!;
            Assert.Equal(EnbTextureFormat.CurrentRetailMissingArgb8,image.Format);
            Assert.Same(image,Binding(shifted)!.CurrentRetailResolvedTexture!.Image);
            Assert.Same(image,assets.Textures.Resolve(binding.LogicalName).Image);
            Assert.Equal(EnbTextureResolutionStatus.MissingResource,assets.Textures.Resolve(missing).Status);
        }
        finally {Directory.Delete(directory,true);}
    }
}
