using System.Buffers.Binary;
using System.Text;
using EnbNext.Net7.Protocol;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

internal static class TestProtocolFixtures
{
    public static byte[] BuildRoster(
        string? slot0Skeleton = null,
        bool slot0Occupied = true,
        uint slot0Shard = 7,
        uint routeShard = 7,
        uint routeMasterIpv4Raw = 0x0100007F,
        ushort routePortRaw = 3802,
        ushort routeGate = 1,
        byte[]? routingHeader = null,
        int routingRecordCount = 2)
    {
        string[] skeletons = [slot0Skeleton ?? string.Empty, string.Empty, string.Empty, string.Empty, string.Empty];
        int total = skeletons.Sum(s => Net7AvatarRosterCodec.SlotMetaSize + Net7AvatarRosterCodec.AvatarDataBaseSize + Encoding.Latin1.GetByteCount(s))
            + Net7AvatarRosterCodec.RoutingHeaderSize
            + routingRecordCount * Net7AvatarRosterCodec.RoutingRecordSize;

        byte[] payload = new byte[total];
        int offset = 0;
        for (int slot = 0; slot < Net7AvatarRosterCodec.SlotCount; slot++)
        {
            Span<byte> meta = payload.AsSpan(offset, Net7AvatarRosterCodec.SlotMetaSize);
            BinaryPrimitives.WriteUInt32BigEndian(meta.Slice(0x00, 4), (uint)slot);
            BinaryPrimitives.WriteUInt32BigEndian(meta.Slice(0x04, 4), slot == 0 ? 1079u : 0u);
            BinaryPrimitives.WriteUInt32BigEndian(meta.Slice(0x08, 4), slot == 0 ? slot0Shard : 0u);
            BinaryPrimitives.WriteUInt32BigEndian(meta.Slice(0x0C, 4), slot == 0 ? 12u : 0u);
            BinaryPrimitives.WriteUInt64BigEndian(meta.Slice(0x10, 8), slot == 0 && slot0Occupied ? 0x0102030405060708UL : 0UL);
            BinaryPrimitives.WriteUInt64BigEndian(meta.Slice(0x18, 8), slot == 0 && slot0Occupied ? 0x1112131415161718UL : 0UL);
            BinaryPrimitives.WriteUInt32BigEndian(meta.Slice(0x20, 4), slot == 0 ? 2u : 0u);
            BinaryPrimitives.WriteUInt32BigEndian(meta.Slice(0x24, 4), slot == 0 ? 3u : 0u);
            BinaryPrimitives.WriteUInt32BigEndian(meta.Slice(0x28, 4), slot == 0 ? 10u : 0u);
            BinaryPrimitives.WriteUInt32BigEndian(meta.Slice(0x2C, 4), slot == 0 ? 11u : 0u);
            BinaryPrimitives.WriteUInt32BigEndian(meta.Slice(0x30, 4), slot == 0 ? 12u : 0u);
            if (slot == 0)
                WriteFixedLatin1(meta.Slice(0x34, 0x51), "Test Sector");
            offset += Net7AvatarRosterCodec.SlotMetaSize;

            string skeleton = skeletons[slot];
            int avatarSize = Net7AvatarRosterCodec.AvatarDataBaseSize + Encoding.Latin1.GetByteCount(skeleton);
            Span<byte> avatar = payload.AsSpan(offset, avatarSize);
            if (slot == 0)
            {
                WriteFixedLatin1(avatar.Slice(0x000, 0x14), "Kestralion");
                WriteFixedLatin1(avatar.Slice(0x014, 0x14), "Tester");
                BinaryPrimitives.WriteUInt32LittleEndian(avatar.Slice(0x028, 4), 0x11223344);
                avatar[0x02C] = 0xAB;
                avatar[0x02D] = 4;
                BinaryPrimitives.WriteUInt32LittleEndian(avatar.Slice(0x02E, 4), 2);
                BinaryPrimitives.WriteUInt32LittleEndian(avatar.Slice(0x032, 4), 5);
                BinaryPrimitives.WriteUInt32LittleEndian(avatar.Slice(0x036, 4), 1);
                WriteFloat(avatar, 0x04C, 1.25f);
                WriteFloat(avatar, 0x050, -2.5f);
                WriteFloat(avatar, 0x054, 3.75f);
                BinaryPrimitives.WriteUInt32LittleEndian(avatar.Slice(0x0B8, 4), 0xA1A2A3A4);
            }

            byte[] skeletonBytes = Encoding.Latin1.GetBytes(skeleton);
            skeletonBytes.CopyTo(avatar.Slice(Net7AvatarRosterCodec.AvatarSkeletonOffset));
            avatar[Net7AvatarRosterCodec.AvatarSkeletonOffset + skeletonBytes.Length] = 0;
            int body = Net7AvatarRosterCodec.AvatarSkeletonOffset + skeletonBytes.Length + 1;
            for (int i = 0; i < 5; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(avatar.Slice(body + i * 4, 4), (uint)(100 + slot * 10 + i));
                BinaryPrimitives.WriteUInt32LittleEndian(avatar.Slice(body + 0x14 + i * 4, 4), (uint)(200 + slot * 10 + i));
            }
            offset += avatarSize;
        }

        byte[] header = routingHeader ?? [0x00, 0x00, 0x00, 0x01];
        if (header.Length != Net7AvatarRosterCodec.RoutingHeaderSize)
            throw new ArgumentException("Routing header must be exactly four bytes.", nameof(routingHeader));
        header.CopyTo(payload, offset);
        offset += Net7AvatarRosterCodec.RoutingHeaderSize;

        for (int i = 0; i < routingRecordCount; i++)
        {
            Span<byte> route = payload.AsSpan(offset, Net7AvatarRosterCodec.RoutingRecordSize);
            if (i == 0)
            {
                WriteFixedLatin1(route.Slice(0x00, 64), "Andromeda");
                BinaryPrimitives.WriteUInt32LittleEndian(route.Slice(0x40, 4), routeShard);
                BinaryPrimitives.WriteUInt32LittleEndian(route.Slice(0x44, 4), routeMasterIpv4Raw);
                BinaryPrimitives.WriteUInt16LittleEndian(route.Slice(0x48, 2), routePortRaw);
                BinaryPrimitives.WriteUInt32LittleEndian(route.Slice(0x4A, 4), 3);
                BinaryPrimitives.WriteUInt32LittleEndian(route.Slice(0x4E, 4), 100);
                BinaryPrimitives.WriteUInt16LittleEndian(route.Slice(0x52, 2), routeGate);
            }
            offset += Net7AvatarRosterCodec.RoutingRecordSize;
        }

        if (offset != payload.Length)
            throw new InvalidOperationException("Fixture encoder geometry mismatch.");
        return payload;
    }

    public static byte[] BuildAvatarSelectionPayload(uint opaqueResult, uint fixedSessionId)
    {
        byte[] payload = new byte[0x18];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), opaqueResult);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0x14, 4), fixedSessionId);
        return payload;
    }

    public static Packet BuildPacket(ushort opcode, ReadOnlySpan<byte> payload)
        => Packet.ForOpcode(opcode, payload.ToArray());

    public static byte[] BuildCurrentGalaxyMap()
    {
        var frames = new List<byte[]>();
        frames.Add(BuildGalaxyFrame(3, Enumerable.Repeat((byte)0xFF, 63).ToArray()));

        for (int i = 0; i < 19; i++)
        {
            using var blob = new MemoryStream();
            WriteU32(blob, (uint)(0x0100 + i));
            WriteU32(blob, (uint)(0x10000000 + i));
            WriteCString(blob, $"SYS{i}");
            for (int f = 0; f < 7; f++)
                WriteF32(blob, i + f / 10f);
            blob.WriteByte((byte)i);
            frames.Add(BuildGalaxyFrame(5, blob.ToArray()));
        }

        var sectorIds = new List<uint>(86);
        for (int i = 0; i < 86; i++)
        {
            uint command = i < 75 ? 6u : i < 82 ? 7u : 8u;
            uint sectorId = (uint)(0x20000000 + i);
            sectorIds.Add(sectorId);
            using var blob = new MemoryStream();
            WriteU32(blob, (uint)(0x30000000 + i));
            WriteU32(blob, sectorId);
            WriteU32(blob, (uint)i);
            WriteCString(blob, $"SEC{i}");
            WriteU32(blob, command);
            WriteF32(blob, i + 0.1f);
            WriteF32(blob, i + 0.2f);
            WriteF32(blob, i + 0.3f);
            frames.Add(BuildGalaxyFrame(command, blob.ToArray()));
        }

        var gateBlobs = new List<byte[]>(199);
        for (int i = 0; i < 199; i++)
        {
            using var blob = new MemoryStream();
            WriteU32(blob, (uint)(0x40000000 + i));
            WriteU32(blob, sectorIds[i % sectorIds.Count]);
            WriteU32(blob, sectorIds[(i + 1) % sectorIds.Count]);
            gateBlobs.Add(blob.ToArray());
        }

        int baseSize = frames.Sum(x => x.Length) + gateBlobs.Sum(x => 12 + x.Length);
        int padding = Net7GalaxyMapCodec.CurrentFileSize - baseSize;
        if (padding < 0)
            throw new InvalidOperationException("Synthetic GalaxyMap fixture exceeded current file size.");
        byte[] firstGate = gateBlobs[0];
        Array.Resize(ref firstGate, firstGate.Length + padding);
        gateBlobs[0] = firstGate;
        frames.AddRange(gateBlobs.Select(blob => BuildGalaxyFrame(9, blob)));

        byte[] file = frames.SelectMany(x => x).ToArray();
        if (file.Length != Net7GalaxyMapCodec.CurrentFileSize || frames.Count != Net7GalaxyMapCodec.CurrentFrameCount)
            throw new InvalidOperationException("Synthetic GalaxyMap fixture geometry mismatch.");
        return file;
    }

    private static byte[] BuildGalaxyFrame(uint command, byte[] blob)
    {
        byte[] frame = new byte[12 + blob.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0, 2), checked((ushort)frame.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), 0x0097);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), command);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8, 4), checked((uint)blob.Length));
        blob.CopyTo(frame, 12);
        return frame;
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteF32(Stream stream, float value)
        => WriteU32(stream, unchecked((uint)BitConverter.SingleToInt32Bits(value)));

    private static void WriteCString(Stream stream, string value)
    {
        stream.Write(Encoding.Latin1.GetBytes(value));
        stream.WriteByte(0);
    }

    private static void WriteFixedLatin1(Span<byte> destination, string value)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(value);
        if (bytes.Length >= destination.Length)
            throw new ArgumentOutOfRangeException(nameof(value));
        bytes.CopyTo(destination);
        destination[bytes.Length] = 0;
    }

    private static void WriteFloat(Span<byte> destination, int offset, float value)
        => BinaryPrimitives.WriteUInt32LittleEndian(
            destination.Slice(offset, 4),
            unchecked((uint)BitConverter.SingleToInt32Bits(value)));
}
