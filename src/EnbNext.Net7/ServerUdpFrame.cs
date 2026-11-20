using System.Buffers.Binary;

namespace EnbNext.Net7;

/// <summary>Current 12-byte Net7 server-facing UDP envelope.</summary>
public sealed record ServerUdpFrame(
    ushort Opcode,
    uint FixedSessionId,
    uint Sequence,
    ReadOnlyMemory<byte> Payload)
{
    public const int HeaderSize = 12;
    public const int MaxPayloadSize = ushort.MaxValue - HeaderSize;

    public ushort TotalLength
    {
        get
        {
            if (Payload.Length > MaxPayloadSize)
            {
                throw new InvalidDataException(
                    $"Net7 UDP payload is {Payload.Length} bytes; maximum is {MaxPayloadSize}.");
            }

            return (ushort)(HeaderSize + Payload.Length);
        }
    }

    public byte[] ToWireBytes()
    {
        ushort totalLength = TotalLength;
        byte[] wire = new byte[totalLength];
        BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(0, 2), totalLength);
        BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(2, 2), Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(4, 4), FixedSessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(8, 4), Sequence);
        Payload.Span.CopyTo(wire.AsSpan(HeaderSize));
        return wire;
    }

    public static ServerUdpFrame Parse(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < HeaderSize)
            throw new InvalidDataException($"Net7 UDP datagram is {datagram.Length} bytes; need at least {HeaderSize}.");

        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(datagram[..2]);
        if (length < HeaderSize)
            throw new InvalidDataException($"Net7 UDP length field {length} is smaller than the {HeaderSize}-byte header.");
        if (length != datagram.Length)
            throw new InvalidDataException($"Net7 UDP length field says {length}, received {datagram.Length} bytes.");

        return new ServerUdpFrame(
            BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(2, 2)),
            BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(8, 4)),
            datagram[HeaderSize..].ToArray());
    }
}
