using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRingHsvTests
{
    [Fact]
    public void InstalledKeyShiftPreservesSourceGeometryOpacityAndTimes()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var bundle=EnbRetailAssetBundle.Load(paths);
        Assert.True(bundle.RetailMixResources!.TryReadResource("sk_pd01p1.w3d",out var bytes,out _));
        var source=EnbRetailRingAsset.Decode(bytes);var shifted=source.ShiftColors(new(0,0,.2f));
        Assert.Same(source,source.ShiftColors(Vector3.Zero));
        Assert.Equal(Vector3.Zero,source.Sample(0,20)!.Color);
        Assert.Equal(new Vector3(.2f),shifted.Sample(0,20)!.Color);
        Assert.Equal(source.Header.Color,shifted.Header.Color); // keys branch leaves static color untouched
        Assert.Equal(source.Tracks[2].Select(k=>k.Time),shifted.Tracks[2].Select(k=>k.Time));
        Assert.Equal(source.Sample(.5f,20)!.Geometry.Positions,shifted.Sample(.5f,20)!.Geometry.Positions);
        Assert.Equal(source.Sample(.5f,20)!.Opacity,shifted.Sample(.5f,20)!.Opacity);
        Assert.Same(source.Tracks[4],shifted.Tracks[4]);
    }

    [Fact]
    public void NoColorKeysShiftStaticColorOnly()
    {
        var bytes=new byte[16+0xa8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes,0x742);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),0x80000000u+(uint)(bytes.Length-8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8),1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12),0xa8);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16+0x44),1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16+0x50),.5f);
        var source=EnbRetailRingAsset.Decode(bytes);var shifted=source.ShiftColors(new(120,0,0));
        Assert.Equal(Vector3.UnitX,source.Header.Color);
        Assert.InRange(Vector3.Distance(Vector3.UnitY,shifted.Header.Color),0,.000001f);
        Assert.Equal(.5f,shifted.Header.Opacity);Assert.Empty(shifted.Tracks);
    }
}
