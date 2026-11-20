using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbDxt3Tests
{
    [Fact]
    public void ExplicitAlphaUsesEveryNibbleAndRgbAlwaysUsesFourColors()
    {
        var bytes=Dds();
        Assert.True(EnbRetailTextureDecoder.TryDecode(bytes,"alpha.dds",out var image));
        Assert.Equal(EnbTextureFormat.DdsDxt3,image.Format);
        Assert.Equal(Enumerable.Range(0,16).Select(i=>(byte)(i*17)),image.Pixels.ToArray().Select(p=>p.A));
        Assert.Equal(new EnbColor32(0,0,255,0),image.GetPixel(0,0));
        Assert.Equal(new EnbColor32(255,0,0,17),image.GetPixel(1,0));
        Assert.Equal(new EnbColor32(85,0,170,34),image.GetPixel(2,0));
        Assert.Equal(new EnbColor32(170,0,85,51),image.GetPixel(3,0));
        Assert.Equal(new[]{4,2,1},image.MipLevels.Select(m=>m.Width));
        Assert.Equal(new byte[]{0,17,68,85},image.MipLevels[1].Pixels.ToArray().Select(p=>p.A));
        Assert.Equal(1,EnbTextureUploadMipPlanner.Compile(image,EnbRenderTextureRuntimeSettings.RetailDefaults).UploadMipCount);
        Assert.False(EnbDdsTexture.TryDecodeDxt3(bytes.AsSpan(0,bytes.Length-1),out _));
        Assert.False(EnbCurrentRetailHsvRuntime.TryRecolorDds(bytes,new(60,0,0),out _));
    }

    [InstalledRetailFact]
    public void InstalledMenaceTextureIsDecodedRatherThanSubstituted()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var texture=assets.Textures.Resolve("sk_menace05.tga");
        Assert.Equal(EnbTextureResolutionStatus.Decoded,texture.Status);
        Assert.NotNull(texture.ResourceLocation);Assert.Null(texture.Diagnostic);
        Assert.Equal(EnbTextureFormat.DdsDxt3,texture.Image!.Format);
        Assert.True(texture.Image.MipLevels.Count>1);
        Assert.All(texture.Image.Pixels.ToArray(),p=>Assert.Equal(0,p.A%17));
        Assert.Same(texture,assets.Textures.ResolveCurrentRetailMissingMeshTexture("sk_menace05.tga",assets.Paths.ClientExecutablePath));
        Assert.NotNull(EnbCurrentRetailRObjEffectAsset.Load(assets,2433).Resource);
        //009A1081: a DXT3 recolor leaves the zeroed same-format allocation unwritten.
        var recolored=assets.Textures.ResolveCurrentRetailHsv("sk_menace05.tga",new(-60,0,0));
        Assert.NotEmpty(recolored.Image!.MipLevels);
        Assert.All(recolored.Image.MipLevels,mip=>Assert.All(mip.Pixels.ToArray(),p=>Assert.Equal(default,p)));
    }

    private static byte[] Dds()
    {
        var bytes=new byte[128+3*16];
        void Word(int offset,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset,4),value);
        Word(0,0x20534444);Word(4,124);Word(8,0xa1007);Word(12,4);Word(16,4);Word(28,3);
        Word(76,32);Word(80,4);Word(84,0x33545844);Word(108,0x401008);
        for(int offset=128;offset<bytes.Length;offset+=16)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset,8),0xfedcba9876543210);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset+8,2),0x001f);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset+10,2),0xf800);
            Word(offset+12,0xe4e4e4e4);
        }
        return bytes;
    }
}
