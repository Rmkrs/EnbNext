using System.Buffers.Binary;

namespace EnbNext.Net7.Presentation;

/// <summary>
/// The two current 0x004C branches are behaviorally distinct, but current
/// evidence does not justify friendlier business names. Keep the branch labels
/// neutral instead of smuggling an interpretation into the domain model.
/// </summary>
public enum Net7ModeTransitionKind
{
    Mode0 = 0,
    Mode1 = 1,
}

public sealed record Net7ModeRequestUpdate(uint Field0, uint Mode);
public sealed record Net7NotifyEmoteUpdate(uint GameId, uint Emote);

/// <summary>
/// Codecs for the final four current inbound packet families. Raw fields retain retail host/little-endian representation unless
/// the current packet explicitly uses a network-order helper.
/// </summary>
public static class Net7ResidualInboundWire
{
    public const int ModeRequestPayloadSize = 8;
    public const int ReferrerResponsePayloadSize = 2;
    public const int WarpIndexPayloadSize = 4;
    public const int NotifyEmotePayloadSize = 8;

    public static Net7ModeRequestUpdate DecodeModeRequest(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, ModeRequestPayloadSize, "0x004C Mode Request");
        return new Net7ModeRequestUpdate(
            BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)));
    }

    public static short DecodeGlobalSetReferrerResponse(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, ReferrerResponsePayloadSize, "0x0078 Global Set Referrer Response");
        ushort wire = BinaryPrimitives.ReadUInt16BigEndian(payload);
        return unchecked((short)wire);
    }

    public static uint DecodeWarpIndex(ReadOnlySpan<byte> payload)
    {
        // Retail 008B1EB0 initializes the field to -1 before the bounded read.
        Span<byte> value = stackalloc byte[WarpIndexPayloadSize];
        value.Fill(0xFF);
        payload[..Math.Min(payload.Length, value.Length)].CopyTo(value);
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }

    public static Net7NotifyEmoteUpdate DecodeNotifyEmote(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, NotifyEmotePayloadSize, "0x00A2 Notify Emote");
        return new Net7NotifyEmoteUpdate(
            BinaryPrimitives.ReadUInt32BigEndian(payload[..4]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)));
    }

    // Retail constructors consume their fields without checking unused packet bytes.
    private static void RequirePrefix(ReadOnlySpan<byte> payload, int expected, string context)
    {
        if (payload.Length < expected)
            throw new InvalidDataException($"{context} payload requires {expected} bytes; got {payload.Length}.");
    }
}
