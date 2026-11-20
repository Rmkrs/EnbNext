using System.Buffers.Binary;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Protocol;

/// <summary>
/// Validated current account-TCP response to server avatar-selection 0x2004.
/// Admits only role-10 0x006F or 0x2009; consumes the opaque
/// four bytes at +0x00 only as an all-FF failure sentinel and fixed ID at +0x14.
/// </summary>
public sealed record Net7AvatarSelectionResponse(
    ushort ResponseOpcode,
    byte[] OpaqueResultBytes,
    uint FixedSessionId)
{
    public const int MinimumPayloadSize = 0x18;

    public static Net7AvatarSelectionResponse DecodeAndValidate(Packet packet)
    {
        Net7AvatarSelectionResponse response = Decode(packet);
        response.ValidateAccepted();
        return response;
    }

    public static Net7AvatarSelectionResponse Decode(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Header.Opcode is not (0x006F or 0x2009))
        {
            throw new InvalidDataException(
                $"TCP role-10 opcode 0x{packet.Header.Opcode:X4} is not an avatar-selection response.");
        }

        ReadOnlySpan<byte> payload = packet.Payload.Span;
        if (payload.Length < MinimumPayloadSize)
        {
            throw new InvalidDataException(
                $"TCP 0x{packet.Header.Opcode:X4} avatar-selection payload is {payload.Length} bytes; need at least 0x18.");
        }

        byte[] opaque = payload[..4].ToArray();
        uint fixedSessionId = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0x14, 4));
        return new Net7AvatarSelectionResponse(packet.Header.Opcode, opaque, fixedSessionId);
    }

    public void ValidateAccepted()
    {
        if (OpaqueResultBytes.AsSpan().SequenceEqual(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }))
        {
            throw new Net7AvatarSelectionRejectedException(
                $"Net7 avatar-selection response 0x{ResponseOpcode:X4} returned the 0xFFFFFFFF failure sentinel.");
        }

        if (FixedSessionId == 0)
        {
            throw new Net7AvatarSelectionRejectedException(
                $"Net7 avatar-selection response 0x{ResponseOpcode:X4} returned fixed/session ID zero.");
        }

    }
}
