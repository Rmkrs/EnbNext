using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Net7.Protocol;

/// <summary>
/// Native semantic state retained from the successful selection seam.
/// It deliberately models only the three nonzero values needed by integrated
/// EnbNext; ToWireBytes reproduces the exact successful 0x40 compatibility image.
/// </summary>
public sealed record Net7SectorJoinClientData(
    uint FixedSessionId,
    uint SelectedSector,
    uint AdminLevel)
{
    public const int WireSize = 0x40;
    private byte[]? _preservedWireImage;

    private Net7SectorJoinClientData(uint fixedSessionId, uint selectedSector, uint adminLevel, byte[] wireImage)
        : this(fixedSessionId, selectedSector, adminLevel)
    {
        _preservedWireImage = wireImage;
    }

    public static Net7SectorJoinClientData CreateSuccessful(uint fixedSessionId, uint selectedSector, uint adminLevel)
    {
        if (fixedSessionId == 0)
            throw new ArgumentOutOfRangeException(nameof(fixedSessionId));
        return new Net7SectorJoinClientData(fixedSessionId, selectedSector, adminLevel);
    }

    public static Net7SectorJoinClientData FromWireBytes(ReadOnlySpan<byte> wire)
    {
        if (wire.Length < WireSize)
            throw new InvalidDataException($"SectorJoinClientData requires {WireSize} bytes, got {wire.Length}.");

        byte[] preserved = wire[..WireSize].ToArray();
        uint fixedSessionId = BinaryPrimitives.ReadUInt32BigEndian(preserved.AsSpan(0x10, 4));
        uint selectedSector = BinaryPrimitives.ReadUInt32BigEndian(preserved.AsSpan(0x14, 4));
        uint adminLevel = BinaryPrimitives.ReadUInt32LittleEndian(preserved.AsSpan(0x1C, 4));
        return new Net7SectorJoinClientData(fixedSessionId, selectedSector, adminLevel, preserved);
    }

    public byte[] ToWireBytes()
    {
        if (_preservedWireImage is not null)
            return _preservedWireImage.ToArray();

        byte[] wire = new byte[WireSize];

        // Exact successful byte image. The proxy writes AdminLevel raw
        // little-endian before retail's generic u32be decode/re-encode round trip,
        // so the retained wire remains little-endian at +0x1C.
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(0x10, 4), FixedSessionId);
        BinaryPrimitives.WriteUInt32BigEndian(wire.AsSpan(0x14, 4), SelectedSector);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(0x1C, 4), AdminLevel);
        Encoding.ASCII.GetBytes("MY_Avatar_Ticket").CopyTo(wire, 0x2C);
        return wire;
    }
}

public static class Net7SectorLoginWire
{
    public const int PayloadSize = 0x89;

    public static byte[] Build(Net7SectorJoinClientData join, uint clientMilliseconds, string? gmName = null)
    {
        ArgumentNullException.ThrowIfNull(join);
        byte[] payload = new byte[PayloadSize];
        join.ToWireBytes().CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x40, 4), clientMilliseconds);

        if (!string.IsNullOrEmpty(gmName))
        {
            // Retail uses strncpy(..., 0x40), so bytes beyond the first
            // 64 never participate in the field. The integrated string convenience
            // API keeps its existing ASCII boundary only for the copied prefix.
            ReadOnlySpan<char> gmPrefix = gmName.AsSpan(0, Math.Min(gmName.Length, 0x40));
            foreach (char c in gmPrefix)
            {
                if (c > 0x7F || c == '\0')
                    throw new ArgumentException("GM name must be NUL-free ASCII for the current 0x41-byte field.", nameof(gmName));
            }

            Encoding.ASCII.GetBytes(gmPrefix, payload.AsSpan(0x44, 0x40));
            // GMName[0x40] and any short-name padding stay zero in the freshly
            // allocated payload, matching the explicit retail terminator.
        }

        // +0x85 trailing raw-host u32 remains zero in the ordinary path.
        return payload;
    }
}
