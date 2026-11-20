using System.Buffers.Binary;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.Tests;

public sealed class Net7ReliabilityWireTests
{
    [Fact]
    public void MissingPacketRequest_IsSequenceThenOneLittleEndian()
    {
        byte[] payload = Net7ReliabilityWire.BuildMissingPacketRequest(0x89ABCDEF);
        Assert.Equal(8, payload.Length);
        Assert.Equal(0x89ABCDEFu, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4)));
    }

    [Fact]
    public void LoginStageAcknowledgement_EchoesStageLittleEndian()
    {
        byte[] payload = Net7ReliabilityWire.BuildLoginStageAcknowledgement(0x10203040);
        Assert.Equal(new byte[] { 0x40, 0x30, 0x20, 0x10 }, payload);
    }

    [Fact]
    public void FastRestartAndGateAck_AreFourZeroBytes()
    {
        Assert.Equal(new byte[4], Net7ReliabilityWire.BuildFastLoginRestartPayload());
        Assert.Equal(new byte[4], Net7ReliabilityWire.BuildGateCacheAcknowledgementPayload());
    }
}
