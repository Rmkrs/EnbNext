using System.Buffers.Binary;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRingPlaybackTests
{
    [Fact]
    public void RenderAdmissionTransitionsResetAndDoNotAccumulateHiddenTime()
    {
        var asset=Asset(1,2);var runtime=new EnbRetailRingPlayback();
        runtime.Advance(asset,false,500000);Assert.Equal(0,runtime.Progress);Assert.False(runtime.Enabled);
        runtime.Advance(asset,true,250);Assert.Equal(.25f,runtime.Progress);
        runtime.Advance(asset,true,250);Assert.Equal(.5f,runtime.Progress);
        runtime.Advance(asset,false,250);Assert.Equal(0,runtime.Progress);
        runtime.Advance(asset,true,100);Assert.InRange(runtime.Progress,.09999f,.10001f);
        runtime.Reset();Assert.False(runtime.Enabled);Assert.Equal(0,runtime.Progress);
    }

    [Fact]
    public void NativeLoopKeepsExactEndpointAndSubtractsOnlyOnceOnLargeStep()
    {
        var asset=Asset(1,2);var runtime=new EnbRetailRingPlayback();
        runtime.Advance(asset,true,1000);Assert.Equal(1,runtime.Progress);
        runtime.Advance(asset,true,250);Assert.Equal(.25f,runtime.Progress);
        runtime.Reset();runtime.Advance(asset,true,5000);Assert.Equal(4,runtime.Progress);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveDurationSelectsEndpoint(float duration)
    {
        var runtime=new EnbRetailRingPlayback();runtime.Advance(Asset(duration,2),true,100);
        Assert.Equal(1,runtime.Progress);
    }

    [Fact]
    public void NonloopingAndEmptyTracksHaveDistinctProgressBehavior()
    {
        var runtime=new EnbRetailRingPlayback();runtime.Advance(Asset(1,0),true,5000);Assert.Equal(5,runtime.Progress);
        runtime.Reset();runtime.Advance(Asset(1,2,false),true,500000);Assert.Equal(0,runtime.Progress);Assert.True(runtime.Enabled);
    }

    private static EnbRetailRingAsset Asset(float duration,uint flags,bool keys=true)
    {
        int total=8+8+0xa8+(keys?26:0);
        var bytes=new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes,0x742);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),0x80000000u+(uint)(total-8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8),1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12),0xa8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20),flags);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16+0x40),duration);
        if(keys)
        {
            int at=16+0xa8;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),3);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at+4),18);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at+8),0x03150809);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at+12),10);
            bytes[at+16]=1;bytes[at+17]=8;
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at+18),1);
        }
        return EnbRetailRingAsset.Decode(bytes);
    }
}
