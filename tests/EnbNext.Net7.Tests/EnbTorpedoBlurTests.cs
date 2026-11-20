using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbTorpedoBlurTests
{
    [Fact]
    public void InstalledTorpedoPreservesCoincidentBlurKeysAndCompleteAssembly()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        Assert.True(assets.RetailMixResources!.TryReadResource("W_Ptorp03b.w3d",out var bytes,out _));
        Assert.True(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes,out var d,out var error),error);
        Assert.Equal(new float[]{0,0,0,0,.09900001f},d.BlurTime!.Keys.Select(k=>k.TimeSeconds));
        Assert.Equal(new float[]{-40,-40,5.9604645e-08f,-50,5.9604645e-08f},d.BlurTime.Keys.Select(k=>k.Value));
        var mixed=EnbCurrentRetailRObjEffectAsset.Load(assets,1574).Mixed!;
        Assert.Equal(2,mixed.Sprites.Count);Assert.Single(mixed.Streaks);
        Assert.Equal("W_Ptorp03b",mixed.Streaks[0].Definition.Name,ignoreCase:true);

        int blurOffset=8;
        while(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(blurOffset))!=0x50c)
            blurOffset+=8+(int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(blurOffset+4))&0x7fffffff);
        // Nondecreasing remains required, as do finite values and exact extent.
        foreach(float invalid in new[]{-1f,float.NaN,float.PositiveInfinity})
        {
            var changed=(byte[])bytes.Clone();
            BinaryPrimitives.WriteSingleLittleEndian(changed.AsSpan(blurOffset+8+0x2c),invalid);
            Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(changed,out _,out _));
        }
        Assert.False(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes.AsSpan(0,bytes.Length-1),out _,out _));
    }
}
