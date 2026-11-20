using System.Buffers.Binary;

namespace EnbNext.Protocol.Net;

/// <summary>
/// Current Net7 TCP frame header: little-endian total length + opcode.
/// This type is shared by the plaintext account connection and ordinary
/// retail-compatible gameplay frames carried by the current server protocol.
/// </summary>
public readonly record struct PacketHeader(ushort Size, ushort Opcode)
{
    public const int WireSize = 4;

    public int PayloadLength => Size - WireSize;

    public static PacketHeader Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < WireSize)
            throw new ArgumentException($"Need {WireSize} bytes for a Net7 TCP header, got {source.Length}.", nameof(source));

        return new PacketHeader(
            BinaryPrimitives.ReadUInt16LittleEndian(source),
            BinaryPrimitives.ReadUInt16LittleEndian(source[2..]));
    }

    public void Write(Span<byte> destination)
    {
        if (destination.Length < WireSize)
            throw new ArgumentException($"Need {WireSize} bytes for a Net7 TCP header, got {destination.Length}.", nameof(destination));

        BinaryPrimitives.WriteUInt16LittleEndian(destination, Size);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], Opcode);
    }
}
