using System.Buffers.Binary;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbSphereHeaderTests
{
    [Fact]
    public void HeaderSeparatesFiveParametersFromTextureAndShaderAndRetainsRawValues()
    {
        var bytes=new byte[0xa4];
        "sphere\0"u8.CopyTo(bytes.AsSpan(8));
        for(int i=0;i<5;i++)BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x60+4*i),i+.25f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x40),float.NaN);
        "sphere.dds\0"u8.CopyTo(bytes.AsSpan(0x74));bytes[0xa3]=0x91;
        var header=EnbCurrentRetailSphereHeader.Read(bytes);
        Assert.Equal("sphere",header.Name);Assert.Equal("sphere.dds",header.Texture);
        Assert.Equal(new[]{.25f,1.25f,2.25f,3.25f,4.25f},header.Parameters60);
        Assert.True(float.IsNaN(header.Duration));Assert.Equal(0x91,header.ShaderBytes[15]);
        Assert.Throws<InvalidDataException>(()=>EnbCurrentRetailSphereHeader.Read(bytes.AsSpan(0,0xa3)));
    }
}
