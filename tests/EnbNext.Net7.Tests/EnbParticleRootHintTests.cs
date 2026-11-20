using System.Buffers.Binary;
using System.Text.Json;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbParticleRootHintTests
{
    [Theory]
    [InlineData("ms02a-pat.w3d")]
    [InlineData("ms03a-pat.w3d")]
    public void InstalledUnhintedParticleRootDecodesWithoutWeakeningPayloadBounds(string name)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.True(assets.RetailMixResources!.TryReadResource(name,out var bytes,out _));
        uint size=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        Assert.Equal(0u,size&0x80000000u);
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes,out var original,out var error),error);
        Assert.Equal(Path.GetFileNameWithoutExtension(name),original.Name,ignoreCase:true);
        var hinted=bytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(hinted.AsSpan(4),size|0x80000000u);
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(hinted,out var withHint,out error),error);
        var options=new JsonSerializerOptions{IncludeFields=true};
        Assert.Equal(JsonSerializer.Serialize(original,options),JsonSerializer.Serialize(withHint,options));
        Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes.AsSpan(0,bytes.Length-1),out _,out _));
        var wrongType=bytes.ToArray();BinaryPrimitives.WriteUInt32LittleEndian(wrongType,0x501);
        Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(wrongType,out _,out _));
        var badChild=bytes.ToArray();BinaryPrimitives.WriteUInt32LittleEndian(badChild.AsSpan(12),uint.MaxValue);
        Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(badChild,out _,out _));
        var trailing=bytes.Concat(new byte[]{0}).ToArray();
        Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(trailing,out _,out _));
    }
}
