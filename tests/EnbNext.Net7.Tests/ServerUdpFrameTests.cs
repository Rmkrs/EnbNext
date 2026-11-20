using System.Buffers.Binary;
using EnbNext.Net7;

namespace EnbNext.Net7.Tests;

public sealed class ServerUdpFrameTests
{
    [Fact]
    public void BootstrapFrame_MatchesCurrentNet7Layout()
    {
        var frame = new ServerUdpFrame(
            Opcode: 0x1000,
            FixedSessionId: 0,
            Sequence: 0,
            Payload: new byte[] { 0xF6, 0x00, 0x00, 0x00 });

        byte[] wire = frame.ToWireBytes();
        Assert.Equal(new byte[]
        {
            0x10, 0x00,
            0x00, 0x10,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0xF6, 0x00, 0x00, 0x00,
        }, wire);

        ServerUdpFrame parsed = ServerUdpFrame.Parse(wire);
        Assert.Equal(frame.Opcode, parsed.Opcode);
        Assert.Equal(frame.FixedSessionId, parsed.FixedSessionId);
        Assert.Equal(frame.Sequence, parsed.Sequence);
        Assert.Equal(frame.Payload.ToArray(), parsed.Payload.ToArray());
    }

    [Fact]
    public void GoldenFrame_IsLittleEndianAndUsesTwelveByteHeader()
    {
        var frame = new ServerUdpFrame(
            0x1234,
            0x11223344,
            0x55667788,
            new byte[] { 0xAA, 0xBB, 0xCC });

        Assert.Equal(new byte[]
        {
            0x0F, 0x00,
            0x34, 0x12,
            0x44, 0x33, 0x22, 0x11,
            0x88, 0x77, 0x66, 0x55,
            0xAA, 0xBB, 0xCC,
        }, frame.ToWireBytes());
    }

    [Fact]
    public void MaximumPayload_EncodesWithoutLengthWrap()
    {
        var payload = new byte[ServerUdpFrame.MaxPayloadSize];
        var frame = new ServerUdpFrame(0x1234, 0, 0, payload);

        byte[] wire = frame.ToWireBytes();

        Assert.Equal(ushort.MaxValue, wire.Length);
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(0, 2)));
    }

    [Fact]
    public void OversizePayload_IsRejectedBeforeEncoding()
    {
        var frame = new ServerUdpFrame(
            0x1234,
            0,
            0,
            new byte[ServerUdpFrame.MaxPayloadSize + 1]);

        Assert.Throws<InvalidDataException>(() => frame.ToWireBytes());
    }

    [Fact]
    public void ParseRejectsDeclaredLengthSmallerThanHeader()
    {
        byte[] wire = new byte[ServerUdpFrame.HeaderSize];
        BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(0, 2), 4);

        Assert.Throws<InvalidDataException>(() => ServerUdpFrame.Parse(wire));
    }

    [Fact]
    public void ParseRejectsDeclaredLengthDifferentFromDatagramLength()
    {
        byte[] wire = new byte[ServerUdpFrame.HeaderSize + 1];
        BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(0, 2), ServerUdpFrame.HeaderSize);

        Assert.Throws<InvalidDataException>(() => ServerUdpFrame.Parse(wire));
    }
}
