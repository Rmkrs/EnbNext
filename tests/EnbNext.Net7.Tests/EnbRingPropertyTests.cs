using System.Buffers.Binary;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbRingPropertyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void NativeKeysStoreValueBeforeTimeAndKeepFileOrder(int components)
    {
        byte[] payload=Fixture(components);
        var keys=EnbCurrentRetailEffectPropertyKeys.Read(payload,components);
        Assert.Equal(2,keys.Count);
        Assert.Equal(Enumerable.Range(1,components).Select(i=>(float)i),keys[0].Value);
        Assert.Equal(.75f,keys[0].Time);
        Assert.Equal(Enumerable.Range(1,components).Select(i=>(float)-i),keys[1].Value);
        Assert.Equal(.25f,keys[1].Time); // inspection must not silently sort authored keys
    }

    [Fact]
    public void UnknownMicrochunksAreSkippedButTruncatedKeysAreRejected()
    {
        var payload=Fixture(2);
        payload[8]=9;
        Assert.Single(EnbCurrentRetailEffectPropertyKeys.Read(payload,2));
        Assert.Throws<InvalidDataException>(()=>EnbCurrentRetailEffectPropertyKeys.Read(payload[..^1],2));
        payload=Fixture(1);
        Assert.Throws<InvalidDataException>(()=>EnbCurrentRetailEffectPropertyKeys.Read(payload,3));
        Assert.Throws<InvalidDataException>(()=>EnbCurrentRetailEffectPropertyKeys.Read(Fixture(3),5));
        Assert.Throws<ArgumentOutOfRangeException>(()=>EnbCurrentRetailEffectPropertyKeys.Read(payload,4));
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(14),1.0144404f);
        Assert.Equal(1.0144404f,EnbCurrentRetailEffectPropertyKeys.Read(payload,1)[0].Time);
    }

    private static byte[] Fixture(int components)
    {
        int length=(components+1)*4, stride=2+length;
        var bytes=new byte[8+stride*2];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes,0x03150809);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),(uint)(stride*2));
        for(int key=0;key<2;key++)
        {
            int at=8+key*stride;bytes[at]=1;bytes[at+1]=(byte)length;
            for(int i=0;i<components;i++)BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at+2+i*4),(key==0?1:-1)*(i+1));
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at+2+components*4),key==0?.75f:.25f);
        }
        return bytes;
    }
}
