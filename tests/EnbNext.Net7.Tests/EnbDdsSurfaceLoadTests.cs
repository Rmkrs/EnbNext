using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbDdsSurfaceLoadTests
{
    private static byte[] Source(uint format=0x31545844,int payload=48)
    {
        var file=new byte[128+payload];
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4),124);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12),8);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16),8);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28),4);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(84),format);
        for(int offset=128;offset+8<=file.Length;offset+=8)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(offset),offset<160?(ushort)0xf800:(ushort)31);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(offset+2),0);
        }
        return file;
    }

    [Fact]
    public void DdsTaskRetainsHsvForEveryActualAllocatedLevel()
    {
        var first=Enumerable.Repeat((byte)0xa5,288).ToArray();
        var second=Enumerable.Repeat((byte)0xa5,80).ToArray();
        Assert.True(EnbCurrentRetailDdsSurfaceLoadRuntime.TryLoad(Source(),0,8,8,2,[first,second],[36,20],new(120,0,0)));
        for(int row=0;row<8;row++)
        {
            for(int x=0;x<8;x++)Assert.Equal(0xff00f800u,BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(row*36+x*4)));
            Assert.All(first.AsSpan(row*36+32,4).ToArray(),value=>Assert.Equal(0xa5,value));
        }
        for(int row=0;row<4;row++)
        {
            for(int x=0;x<4;x++)Assert.Equal(0xfff80000u,BinaryPrimitives.ReadUInt32LittleEndian(second.AsSpan(row*20+x*4)));
            Assert.All(second.AsSpan(row*20+16,4).ToArray(),value=>Assert.Equal(0xa5,value));
        }
    }

    [Theory]
    [InlineData(0x32545844u,2u,8u,8u,0u)] //Generic DXT2 source returns without writing.
    [InlineData(0x31545844u,2u,16u,8u,0u)] //Unequal dimensions are a successful DDS task.
    [InlineData(0x31545844u,22u,8u,8u,0u)] //Unsupported compressed conversion.
    [InlineData(0x31545844u,2u,8u,8u,100u)] //Loaded payload underflow: no native allocation.
    public void SuccessfulDdsTaskDoesNotFallThroughWhenNoPixelsAreWritten(uint format,uint destination,uint width,uint height,uint reduction)
    {
        var storage=Enumerable.Repeat((byte)0xa5,512).ToArray();
        Assert.True(EnbCurrentRetailDdsSurfaceLoadRuntime.TryLoad(Source(format),reduction,width,height,destination,[storage],[64],Vector3.Zero));
        Assert.All(storage,value=>Assert.Equal(0xa5,value));
    }

    [Fact]
    public void PayloadFailureFallsThroughButEmptyAllocationStillSucceeds()
    {
        Assert.False(EnbCurrentRetailDdsSurfaceLoadRuntime.TryLoad(Source(payload:0),0,8,8,2,[],[],Vector3.Zero));
        Assert.True(EnbCurrentRetailDdsSurfaceLoadRuntime.TryLoad(Source(),0,8,8,2,[],[],Vector3.Zero));
    }

    [Fact]
    public void NoncompressedSameFormatUsesNativeSizeTableAndIgnoresPitch()
    {
        var file=Source(21); //SDK21 maps to native2; native table size is four blocks, not a modern ARGB image size.
        var storage=Enumerable.Repeat((byte)0xa5,256).ToArray();
        Assert.True(EnbCurrentRetailDdsSurfaceLoadRuntime.TryLoad(file,0,8,8,2,[storage],[32],Vector3.Zero));
        Assert.Equal(file.AsSpan(128,4).ToArray(),storage.AsSpan(0,4).ToArray());
        Assert.All(storage[4..],value=>Assert.Equal(0xa5,value));
        Array.Fill(storage,(byte)0xa5);
        Assert.True(EnbCurrentRetailDdsSurfaceLoadRuntime.TryLoad(file,0,8,8,2,[storage],[32],new(120,0,0)));
        Assert.All(storage,value=>Assert.Equal(0xa5,value));
    }
}
