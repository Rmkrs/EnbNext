using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Net7.Protocol;

/// <summary>Current 0x0070 GlobalAvatarList decoder.</summary>
public static class Net7AvatarRosterCodec
{
    public const int CurrentFixedPayloadSize = 0x7FA;
    public const int SlotCount = 5;
    public const int SlotMetaSize = 0x85;
    public const int AvatarDataBaseSize = 0xF1;
    public const int AvatarSkeletonOffset = 0xC8;
    public const int RoutingHeaderSize = 4;
    public const int RoutingRecordSize = 0x54;
    public const int CurrentFixedRoutingRecordSlots = 2;
    public const int MinimumPayloadSize = SlotCount * (SlotMetaSize + AvatarDataBaseSize) + RoutingHeaderSize; // 0x752

    public static Net7AvatarRoster Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < MinimumPayloadSize)
        {
            throw new InvalidDataException(
                $"GlobalAvatarList 0x0070 payload is {payload.Length} bytes; minimum structural size is {MinimumPayloadSize} (0x{MinimumPayloadSize:X}).");
        }

        var slots = new Net7AvatarSlot[SlotCount];
        int offset = 0;
        for (int i = 0; i < SlotCount; i++)
        {
            RequireRemaining(payload, offset, SlotMetaSize, $"slot {i} descriptor");
            Net7AvatarSlotMeta meta = DecodeMeta(payload.Slice(offset, SlotMetaSize));
            offset += SlotMetaSize;

            (Net7AvatarData avatar, int consumed) = DecodeAvatarData(payload[offset..], $"GlobalAvatarList slot {i}");
            offset += consumed;
            slots[i] = new Net7AvatarSlot(meta, avatar);
        }

        RequireRemaining(payload, offset, RoutingHeaderSize, "routing header");
        byte[] routingHeaderRaw = payload.Slice(offset, RoutingHeaderSize).ToArray();
        offset += RoutingHeaderSize;

        // 00862D90 uses a signed network count and reuses one initialized
        // routing record. 0083C7C0 preserves bytes not supplied by a short read.
        int routingCount = BinaryPrimitives.ReadInt32BigEndian(routingHeaderRaw);
        var routing = new List<Net7GalaxyRoutingRecord>();
        Span<byte> routeBytes = stackalloc byte[RoutingRecordSize];
        routeBytes.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(routeBytes[0x52..], 1);
        for (int i = 0; i < routingCount; i++)
        {
            int available = Math.Min(RoutingRecordSize, payload.Length - offset);
            payload.Slice(offset, available).CopyTo(routeBytes);
            offset += available;
            routing.Add(DecodeRouting(routeBytes));
        }

        return new Net7AvatarRoster(slots, routingHeaderRaw, routing);
    }

    private static Net7AvatarSlotMeta DecodeMeta(ReadOnlySpan<byte> data)
    {
        return new Net7AvatarSlotMeta(
            Slot: BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x00, 4)),
            Sector: BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x04, 4)),
            ShardId: BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x08, 4)),
            SaveCount: BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x0C, 4)),
            AvatarId: BinaryPrimitives.ReadUInt64BigEndian(data.Slice(0x10, 8)),
            AccountId: BinaryPrimitives.ReadUInt64BigEndian(data.Slice(0x18, 8)),
            AdminLevel: BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x20, 4)),
            GmFlag: BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x24, 4)),
            CombatLevel: BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x28, 4)),
            ExploreLevel: BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x2C, 4)),
            TradeLevel: BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0x30, 4)),
            SectorName: ReadFixedLatin1(data.Slice(0x34, 0x51)));
    }

    /// <summary>
    /// Decode one current serialized AvatarData value. Its length is
    /// dynamic by the NUL-terminated Skeleton length; other messages reuse the exact same
    /// serializer contract for 0x0061/0x0085 rather than inventing a fixed size.
    /// </summary>
    public static (Net7AvatarData Avatar, int Consumed) DecodeAvatarData(ReadOnlySpan<byte> data, string context = "AvatarData")
    {
        if (data.Length <= AvatarSkeletonOffset)
            throw new InvalidDataException($"{context} is truncated before Skeleton.");

        int skeletonLength = data[AvatarSkeletonOffset..].IndexOf((byte)0);
        bool terminated = skeletonLength >= 0;
        if (!terminated)
            skeletonLength = data.Length - AvatarSkeletonOffset;
        int bodyWeightOffset = AvatarSkeletonOffset + skeletonLength + (terminated ? 1 : 0);
        int headWeightOffset = bodyWeightOffset + 0x14;
        string skeleton = Encoding.Latin1.GetString(data.Slice(AvatarSkeletonOffset, skeletonLength));
        // 004AAE90 retains an unterminated string through the packet end.
        // 004BF550 then reads weights into the constructor's existing values.
        // 004C0310 initializes each weight vector to (1,0,0,0,0).
        var avatar = new Net7AvatarData(
            FirstName: ReadFixedLatin1(data.Slice(0x000, 0x14)),
            LastName: ReadFixedLatin1(data.Slice(0x014, 0x14)),
            AvatarType: ReadRaw32(data, 0x028),
            UnknownU8: data[0x02C],
            AvatarVersion: data[0x02D],
            Race: ReadRaw32(data, 0x02E),
            Profession: ReadRaw32(data, 0x032),
            Gender: ReadRaw32(data, 0x036),
            MoodType: ReadRaw32(data, 0x03A),
            Personality: data[0x03E],
            Nlp: data[0x03F],
            BodyType: data[0x040],
            PantsType: data[0x041],
            HeadType: data[0x042],
            HairNum: data[0x043],
            EarNum: data[0x044],
            GoggleNum: data[0x045],
            BeardNum: data[0x046],
            WeaponHipNum: data[0x047],
            WeaponUniqueNum: data[0x048],
            WeaponBackNum: data[0x049],
            HeadTextureNum: data[0x04A],
            TattooTextureNum: data[0x04B],
            TattooOffset: ReadFloat32Array(data, 0x04C, 3),
            HairColor: ReadFloat32Array(data, 0x058, 3),
            BeardColor: ReadFloat32Array(data, 0x064, 3),
            EyeColor: ReadFloat32Array(data, 0x070, 3),
            SkinColor: ReadFloat32Array(data, 0x07C, 3),
            ShirtPrimaryColor: ReadFloat32Array(data, 0x088, 3),
            ShirtSecondaryColor: ReadFloat32Array(data, 0x094, 3),
            PantsPrimaryColor: ReadFloat32Array(data, 0x0A0, 3),
            PantsSecondaryColor: ReadFloat32Array(data, 0x0AC, 3),
            ShirtPrimaryMetal: ReadRaw32(data, 0x0B8),
            ShirtSecondaryMetal: ReadRaw32(data, 0x0BC),
            PantsPrimaryMetal: ReadRaw32(data, 0x0C0),
            PantsSecondaryMetal: ReadRaw32(data, 0x0C4),
            Skeleton: skeleton,
            BodyWeight: ReadWeights(data, bodyWeightOffset),
            HeadWeight: ReadWeights(data, headWeightOffset));

        return (avatar, Math.Min(data.Length, headWeightOffset + 0x14));
    }

    private static uint[] ReadWeights(ReadOnlySpan<byte> data, int offset)
    {
        Span<byte> bytes = stackalloc byte[20];
        bytes.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x3f800000);
        if (offset < data.Length)
            data.Slice(offset, Math.Min(bytes.Length, data.Length - offset)).CopyTo(bytes);
        return ReadRaw32Array(bytes, 0, 5);
    }

    private static Net7GalaxyRoutingRecord DecodeRouting(ReadOnlySpan<byte> data)
    {
        // GalaxyRoutingRecord is raw current-x86 serialization: host numeric
        // fields remain little-endian bytes at this layer.
        return new Net7GalaxyRoutingRecord(
            GalaxyName: ReadFixedLatin1(data.Slice(0x00, 64)),
            ShardId: BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x40, 4)),
            MasterIpv4Raw: BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x44, 4)),
            RoutingPortRaw: BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0x48, 2)),
            CurrentLoad: BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x4A, 4)),
            Capacity: BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x4E, 4)),
            EnabledSelectableGate: BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0x52, 2)));
    }

    private static uint ReadRaw32(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));

    private static uint[] ReadRaw32Array(ReadOnlySpan<byte> data, int offset, int count)
    {
        var result = new uint[count];
        for (int i = 0; i < count; i++)
            result[i] = ReadRaw32(data, offset + i * 4);
        return result;
    }

    private static float[] ReadFloat32Array(ReadOnlySpan<byte> data, int offset, int count)
    {
        var result = new float[count];
        for (int i = 0; i < count; i++)
        {
            uint bits = ReadRaw32(data, offset + i * 4);
            result[i] = BitConverter.Int32BitsToSingle(unchecked((int)bits));
        }
        return result;
    }

    private static string ReadFixedLatin1(ReadOnlySpan<byte> data)
    {
        int nul = data.IndexOf((byte)0);
        if (nul < 0)
            nul = data.Length;
        return Encoding.Latin1.GetString(data[..nul]);
    }

    private static void RequireRemaining(ReadOnlySpan<byte> payload, int offset, int length, string description)
    {
        if (offset < 0 || length < 0 || offset > payload.Length - length)
        {
            throw new InvalidDataException(
                $"GlobalAvatarList is truncated while reading {description}: need {length} bytes at 0x{offset:X}, payload length is 0x{payload.Length:X}.");
        }
    }
}
