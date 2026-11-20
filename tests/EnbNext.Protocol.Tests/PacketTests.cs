using System.Buffers.Binary;
using EnbNext.Protocol.Net;

namespace EnbNext.Protocol.Tests;

public sealed class PacketTests
{
    [Fact]
    public void GoldenTcpFrame_IsLittleEndian()
    {
        Packet packet = Packet.ForOpcode(0x1234, new byte[] { 0xAA, 0xBB, 0xCC });

        Assert.Equal(new byte[]
        {
            0x07, 0x00,
            0x34, 0x12,
            0xAA, 0xBB, 0xCC,
        }, packet.ToWireBytes());
    }

    [Fact]
    public void MaximumPayload_UsesMaximumU16FrameLength()
    {
        Packet packet = Packet.ForOpcode(0x1234, new byte[Packet.MaxPayloadSize]);
        byte[] wire = packet.ToWireBytes();

        Assert.Equal(ushort.MaxValue, wire.Length);
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(0, 2)));
    }

    [Fact]
    public void OversizePayload_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            Packet.ForOpcode(0x1234, new byte[Packet.MaxPayloadSize + 1]));
    }

    [Fact]
    public void HeaderPayloadMismatch_IsRejectedBeforeWrite()
    {
        var packet = new Packet(
            new PacketHeader(4, 0x1234),
            new byte[] { 0xAA });

        Assert.Throws<InvalidDataException>(() => packet.ToWireBytes());
    }
}
