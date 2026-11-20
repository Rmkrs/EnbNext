namespace EnbNext.Protocol.Net;

/// <summary>One complete 4-byte-header Net7/retail-compatible frame.</summary>
public sealed record Packet(PacketHeader Header, ReadOnlyMemory<byte> Payload)
{
    public const int MaxPayloadSize = ushort.MaxValue - PacketHeader.WireSize;

    public static Packet ForOpcode(ushort opcode, ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > MaxPayloadSize)
        {
            throw new InvalidDataException(
                $"Net7 TCP payload is {payload.Length} bytes; maximum is {MaxPayloadSize}.");
        }

        ushort total = (ushort)(PacketHeader.WireSize + payload.Length);
        return new Packet(new PacketHeader(total, opcode), payload);
    }

    public byte[] ToWireBytes()
    {
        int expectedSize = PacketHeader.WireSize + Payload.Length;
        if (Header.Size != expectedSize)
        {
            throw new InvalidDataException(
                $"Net7 TCP frame header declares {Header.Size} bytes, but header + payload is {expectedSize} bytes.");
        }

        byte[] wire = new byte[Header.Size];
        Header.Write(wire);
        Payload.Span.CopyTo(wire.AsSpan(PacketHeader.WireSize));
        return wire;
    }
}
