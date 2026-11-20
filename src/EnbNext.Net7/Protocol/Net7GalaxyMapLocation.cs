using System.Buffers.Binary;
using System.Text;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Protocol;

/// <summary>Server command 4, distinct from local map records and the 2010 sentinel.</summary>
public sealed record Net7GalaxyMapLocation(
    uint ReplyChannel, string SystemName, string SectorName, string LocationDetail, uint RecordCount);

public static class Net7GalaxyMapLocationWire
{
    // 0098 has 13 bytes of fields plus a 4-byte length before the presence bits.
    public const uint MaximumRecordCount = (ServerUdpFrame.MaxPayloadSize - 17) * 8u;

    public static Net7GalaxyMapLocation Decode(ReadOnlySpan<byte> payload)
    {
        uint command = Net7GalaxyMapUpdates.Command(payload, out ReadOnlySpan<byte> blob);
        // Net7Session routes only command 4 here; other commands are map updates.
        if (command != 4)
            throw new ArgumentException($"GalaxyMap command {command} is not a location request.", nameof(payload));
        // 00870FD0 copies only the declared view. Physical trailing bytes do
        // not supply missing values in that view.
        uint channel = ReadUInt32(ref blob);
        string system = ReadCString(ref blob);
        string sector = ReadCString(ref blob);
        string detail = ReadCString(ref blob);
        uint count = ReadUInt32(ref blob);
        return new(channel, system, sector, detail, count);
    }

    public static byte[] BuildPresenceReply(Net7GalaxyMapLocation location, Net7GalaxyMapData map, bool showAll = false)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(map);
        if (location.RecordCount > MaximumRecordCount)
            throw new InvalidDataException("GalaxyMap presence reply exceeds the UDP envelope bound.");
        int bytes = (int)((location.RecordCount + 7) / 8);
        byte[] data = new byte[4 + bytes];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)bytes);
        // 00836960 indexes AllData by the first record dword (+8). These are
        // map-record indexes. This reply reports presence, independently of
        // command 3's known-sector bitset using the same index namespace.
        IEnumerable<uint> indexes = map.AllData is { } slots ? slots.Keys :
            map.Systems.Select(r => r.Field0).Concat(map.Sectors.Select(r => r.Field0))
                .Concat(map.Gates.Select(r => r.GateHashId));
        foreach (uint id in indexes)
            if (id < location.RecordCount)
                data[4 + (int)(id >> 3)] |= (byte)(1 << (int)(id & 7));
        // 007A4C50 supplies type 0, inbound channel and the retained +98 flag.
        // 007A4830 initializes that flag from Galaxymap_Show_All (default false).
        return Net7GameplayWire.BuildGalaxyMap2(0, data, location.ReplyChannel, showAll);
    }

    private static uint ReadUInt32(ref ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 4) throw new InvalidDataException("GalaxyMap command 4 is truncated before a u32 value.");
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(blob);
        blob = blob[4..];
        return value;
    }

    private static string ReadCString(ref ReadOnlySpan<byte> blob)
    {
        int end = blob.IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException("GalaxyMap command 4 string has no NUL terminator.");
        string value = Encoding.Latin1.GetString(blob[..end]);
        blob = blob[(end + 1)..];
        return value;
    }
}
