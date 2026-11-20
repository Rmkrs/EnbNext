using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbCompressedSurfaceLoadTests
{
    [Theory]
    [InlineData(0,0)]
    [InlineData(16,3)]
    public void MissingPayloadAllocationPreservesDestinationEvenAfterSuccessfulNativeLoad(int payload,uint reduction)
    {
        var file=Source(256,256,1,0x31545844,payload);
        Assert.True(EnbCurrentRetailDdsSourceState.TryRead(file,reduction,out var state));
        Assert.Equal(payload!=0,state!.LoadReturnedSuccess);
        Assert.False(state.HasPixelAllocation);
        var destination=Enumerable.Repeat((byte)0x7b,32).ToArray();
        Assert.False(EnbCurrentRetailCompressedSurfaceLoadRuntime.LoadMip(file,state,0,21,4,4,destination,Vector3.UnitX));
        Assert.All(destination,value=>Assert.Equal(0x7b,value));
    }

    private static byte[] Source(uint width,uint height,uint levels,uint format,int payload)
    {
        var file=new byte[128+payload];
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4),124);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12),height);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16),width);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28),levels);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(84),format);
        for(int i=128;i<file.Length;i++)file[i]=(byte)i;
        return file;
    }

    [Theory]
    [InlineData(8,4,20)]
    [InlineData(4,8,20)]
    [InlineData(8,4,21)]
    [InlineData(4,8,21)]
    [InlineData(4,4,22)]
    [InlineData(4,4,23)]
    [InlineData(4,4,24)]
    public void DimensionMismatchAndOtherCompressedDestinationsPreserveIncomingStorage(uint width,uint height,uint format)
    {
        var file=Source(4,4,1,0x31545844,8);
        Assert.True(EnbCurrentRetailDdsSourceState.TryRead(file,0,out var state));
        var destination=Enumerable.Repeat((byte)0x7b,128).ToArray();
        Assert.False(EnbCurrentRetailCompressedSurfaceLoadRuntime.LoadMip(file,state!,0,format,width,height,destination,new(120,.1f,.2f)));
        Assert.All(destination,value=>Assert.Equal(0x7b,value));
    }

    [Theory]
    [InlineData(0x32545844)]
    [InlineData(0x33545844)]
    [InlineData(0x34545844)]
    public void Dxt2Through4CopiesAtZeroShiftAndLeavesStorageAtNonzeroShift(uint sdkFormat)
    {
        var file=Source(4,4,1,sdkFormat,16);
        Assert.True(EnbCurrentRetailDdsSourceState.TryRead(file,0,out var state));
        var destination=Enumerable.Repeat((byte)0x7b,32).ToArray();
        Assert.False(EnbCurrentRetailCompressedSurfaceLoadRuntime.LoadMip(file,state!,0,state!.Format,4,4,destination,Vector3.UnitX));
        Assert.All(destination,value=>Assert.Equal(0x7b,value));
        Assert.True(EnbCurrentRetailCompressedSurfaceLoadRuntime.LoadMip(file,state,0,state.Format,4,4,destination,Vector3.Zero));
        Assert.Equal(file[128..],destination[..16]);
        Assert.All(destination[16..],value=>Assert.Equal(0x7b,value));
    }

    [Fact]
    public void ReducedMipCopyUsesNativeLevelSizesAndOffsets()
    {
        var file=Source(64,64,7,0x31545844,2736);
        Assert.True(EnbCurrentRetailDdsSourceState.TryRead(file,1,out var state));
        Assert.Equal(2176u,state!.PixelOffset);
        uint[] widths=[32,16,8,4];int[] offsets=[2176,2688,2816,2848],sizes=[512,128,32,8];
        for(uint level=0;level<4;level++)
        {
            var destination=Enumerable.Repeat((byte)0xa5,sizes[level]+16).ToArray();
            Assert.True(EnbCurrentRetailCompressedSurfaceLoadRuntime.LoadMip(file,state,level,20,widths[level],widths[level],destination,Vector3.Zero));
            Assert.Equal(file.AsSpan(offsets[level],sizes[level]).ToArray(),destination[..sizes[level]]);
            Assert.All(destination[sizes[level]..],value=>Assert.Equal(0xa5,value));
        }
    }
}
