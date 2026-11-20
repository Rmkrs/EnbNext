using System.Buffers.Binary;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Protocol;

public readonly record struct Net7MasterLoginResponse(uint RetainedMasterResponseDword, ushort CandidateGameplayPort)
{
    public static Net7MasterLoginResponse Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 6)
            throw new InvalidDataException($"Master-login UDP 0x2009 payload is {payload.Length} bytes; need at least 6.");

        return new Net7MasterLoginResponse(
            BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2)));
    }
}

public static class Net7EmbeddedClientFrame
{
    /// <summary>
    /// Exact direct-outer 0x2010 envelope. Trailing bytes outside the
    /// declared inner frame are ignored by the current path.
    /// </summary>
    public static Packet Decode(ReadOnlyMemory<byte> outerPayload)
    {
        ReadOnlySpan<byte> payload = outerPayload.Span;
        if (payload.Length < PacketHeader.WireSize)
            throw new InvalidDataException("UDP 0x2010 payload is shorter than the required 4-byte embedded frame header.");

        ushort innerLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[..2]);
        ushort innerOpcode = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
        if (innerLength < PacketHeader.WireSize || innerLength > 0x7FFF)
            throw new InvalidDataException($"UDP 0x2010 embedded length 0x{innerLength:X4} is outside the valid 4..0x7FFF range.");
        if (innerLength > payload.Length)
            throw new InvalidDataException($"UDP 0x2010 declares {innerLength} embedded bytes but carries only {payload.Length}.");

        byte[] innerPayload = payload.Slice(PacketHeader.WireSize, innerLength - PacketHeader.WireSize).ToArray();
        return new Packet(new PacketHeader(innerLength, innerOpcode), innerPayload);
    }
}

public static class Net7SectorEntryWire
{
    public static byte[] BuildRequestTime(uint timeSent)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, timeSent);
        return payload;
    }

    public static byte[] BuildStartAck(uint playerId)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, playerId);
        return payload;
    }

    public static byte[] BuildFixedSessionPayload(uint fixedSessionId)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, fixedSessionId);
        return payload;
    }

    /// <summary>Exact signed selector for the synthetic Start companion.</summary>
    public static ushort SelectStartCompanionOpcode(uint selectedSector)
        => unchecked((int)selectedSector) <= 9999 ? (ushort)0x3004 : (ushort)0x3008;

    public static uint DecodeStartPlayerId(ReadOnlySpan<byte> payload)
    {
        // Retail 008A66A0 reads the ID without requiring exhaustion of the payload.
        if (payload.Length < 4)
            throw new InvalidDataException($"Start UDP 0x0005 payload needs 4 bytes; got {payload.Length}.");
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }
}
