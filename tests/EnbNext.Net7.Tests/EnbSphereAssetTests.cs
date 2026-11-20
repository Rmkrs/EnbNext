using System.Buffers.Binary;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSphereAssetTests
{
    [Fact]
    public void HeaderOnlyAndKeyedAssetsPreserveStaticAndClockMetadata()
    {
        var asset=EnbRetailSphereAsset.Decode(Chunk(0x741,Chunk(1,Header())));
        Assert.False(asset.HasPropertyKeys);Assert.False(asset.HasScaleKeys);
        Assert.Equal(System.Numerics.Vector3.One,asset.Properties.Evaluate(1).Scale);
        var micro=new byte[18];micro[0]=1;micro[1]=16;
        for(int i=0;i<3;i++)BinaryPrimitives.WriteSingleLittleEndian(micro.AsSpan(2+i*4),2);
        var scale=Chunk(4,Chunk(0x03150809,micro));
        asset=EnbRetailSphereAsset.Decode(Chunk(0x741,[..Chunk(1,Header()),..scale]));
        Assert.True(asset.HasPropertyKeys);Assert.True(asset.HasScaleKeys);
        Assert.Equal(new System.Numerics.Vector3(2),asset.Properties.Evaluate(0).Scale);
    }

    [Fact]
    public void UnknownChunksAndRepeatedHeaderUseLastAuthoredValues()
    {
        var first=Header();var last=Header();
        BinaryPrimitives.WriteSingleLittleEndian(first.AsSpan(0x50),.25f);
        BinaryPrimitives.WriteSingleLittleEndian(last.AsSpan(0x50),.75f);
        var asset=EnbRetailSphereAsset.Decode(Chunk(0x741,[..Chunk(6,[1,2,3]),..Chunk(1,first),
            ..Chunk(0xffff,[]),..Chunk(1,last),..Chunk(6,[4])]));
        Assert.Equal(.75f,asset.Header.Opacity);
        Assert.Equal(.75f,asset.Properties.Evaluate(0).Opacity);
    }

    [Theory]
    [InlineData(2,3)] [InlineData(3,1)] [InlineData(4,3)] [InlineData(5,5)]
    public void RepeatedPropertyReplacesPriorKeysIncludingAnEmptyLastChunk(uint id,int width)
    {
        byte[] Key(float value)
        {
            var micro=new byte[2+(width+1)*4];micro[0]=1;micro[1]=(byte)((width+1)*4);
            for(int i=0;i<width;i++)BinaryPrimitives.WriteSingleLittleEndian(micro.AsSpan(2+i*4),value);
            return Chunk(id,Chunk(0x03150809,micro));
        }
        var expected=EnbRetailSphereAsset.Decode(Chunk(0x741,[..Chunk(1,Header()),..Key(.75f)]));
        var actual=EnbRetailSphereAsset.Decode(Chunk(0x741,[..Chunk(1,Header()),..Key(.25f),..Key(.75f)]));
        Assert.Equal(expected.Properties.Evaluate(0),actual.Properties.Evaluate(0));
        var empty=EnbRetailSphereAsset.Decode(Chunk(0x741,[..Chunk(1,Header()),..Key(.25f),..Chunk(id,[])]));
        Assert.False(empty.HasPropertyKeys);
        Assert.Equal(EnbRetailSphereAsset.Decode(Chunk(0x741,Chunk(1,Header()))).Properties.Evaluate(0),empty.Properties.Evaluate(0));
    }

    [Fact]
    public void MalformedOrUnsupportedResourcesFailBeforeSampling()
    {
        byte[] good=Chunk(0x741,Chunk(1,Header()));
        foreach(var bytes in new byte[][]{good[..^1],[..good,0],Chunk(0x742,Chunk(1,Header())),Chunk(0x741,[])})
            Assert.Throws<InvalidDataException>(()=>EnbRetailSphereAsset.Decode(bytes));
        var header=Header();BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(0x28),float.NaN);
        Assert.Throws<InvalidDataException>(()=>EnbRetailSphereAsset.Decode(Chunk(0x741,Chunk(1,header))));
        header=Header();BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4),16);
        Assert.Throws<InvalidDataException>(()=>EnbRetailSphereAsset.Decode(Chunk(0x741,Chunk(1,header))));
    }

    private static byte[] Header()
    {
        var bytes=new byte[0xa4];
        foreach(int offset in new[]{0x34,0x38,0x3c,0x40,0x50,0x54,0x58,0x5c,0x6c,0x70})
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset),1);
        return bytes;
    }
    private static byte[] Chunk(uint id,byte[] payload)
    {
        var bytes=new byte[payload.Length+8];BinaryPrimitives.WriteUInt32LittleEndian(bytes,id);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),(uint)payload.Length|0x80000000);
        payload.CopyTo(bytes,8);return bytes;
    }
}
