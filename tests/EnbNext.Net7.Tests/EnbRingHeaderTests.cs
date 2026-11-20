using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbRingHeaderTests
{
    [Fact]
    public void NativeHeaderOffsetsRemainDistinctFromDefinitionObjectOffsets()
    {
        var bytes=new byte[0xa8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes,0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),3);
        Encoding.ASCII.GetBytes("test-ring").CopyTo(bytes,8);
        for(int at=0x28;at<0x74;at+=4)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at),at);
        Encoding.ASCII.GetBytes("ring-texture.tga").CopyTo(bytes,0x74);
        for(int i=0;i<16;i++)bytes[0x94+i]=(byte)(i+1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0xa4),7);
        var h=EnbCurrentRetailRingHeader.Read(bytes);
        Assert.Equal(0x12345678u,h.RawVersion);Assert.Equal(3u,h.Flags);Assert.Equal("test-ring",h.Name);
        Assert.Equal(new Vector3(0x28,0x2c,0x30),h.Center);
        Assert.Equal(new Vector3(0x34,0x38,0x3c),h.AuthoredExtent);
        Assert.Equal(0x40,h.Duration);Assert.Equal(new Vector3(0x44,0x48,0x4c),h.Color);
        Assert.Equal(0x50,h.Opacity);Assert.Equal(new Vector2(0x54,0x58),h.FirstRadiusMultiplier);
        Assert.Equal(new Vector2(0x5c,0x60),h.SecondRadiusMultiplier);
        Assert.Equal(new Vector2(0x64,0x68),h.FirstRadii);Assert.Equal(new Vector2(0x6c,0x70),h.SecondRadii);
        Assert.Equal("ring-texture.tga",h.Texture);Assert.Equal(7,h.Repeats);
        Assert.Equal(Enumerable.Range(1,16).Select(i=>(byte)i),h.ShaderBytes);
    }

    [Fact]
    public void InspectionRejectsTruncationAndDoesNotNormalizeAuthoredValues()
    {
        Assert.Throws<InvalidDataException>(()=>EnbCurrentRetailRingHeader.Read(new byte[0xa7]));
        var bytes=new byte[0xac];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x64),-2);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x50),float.NaN);
        var h=EnbCurrentRetailRingHeader.Read(bytes);
        Assert.Equal(-2,h.FirstRadii.X);Assert.True(float.IsNaN(h.Opacity));
    }
}
