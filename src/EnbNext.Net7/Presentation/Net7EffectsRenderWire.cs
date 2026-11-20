using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace EnbNext.Net7.Presentation;

public enum Net7EffectGeometryKind
{
    Object = 0,
    Point = 1,
    ObjectToObject = 2,
    ObjectToEffect = 3,
    ObjectToPoint = 4,
    ObjectToObjectDurationLinked = 5,
}

public sealed record Net7PointEffectUpdate(
    uint EffectId,
    uint Timestamp,
    Vector3 Location,
    short Duration,
    ushort EffectDefinitionId,
    float Scale,
    Vector3 HsvShift);

public sealed record Net7ObjectToEffectEffectUpdate(
    uint EffectId,
    uint Timestamp,
    uint SourceGameId,
    string SourceBoneChain,
    uint TargetGameId,
    short Duration,
    ushort EffectDefinitionId,
    Vector3 TargetOffset,
    short AuxiliaryEffectFieldA,
    short AuxiliaryEffectFieldB,
    float Scale,
    Vector3 HsvShift,
    float Speedup);

public sealed record Net7ObjectToPointEffectUpdate(
    uint EffectId,
    uint Timestamp,
    uint SourceGameId,
    string SourceBoneChain,
    Vector3 TargetPoint,
    short Duration,
    ushort EffectDefinitionId,
    float Scale,
    Vector3 HsvShift,
    float Speedup);

public sealed record Net7ObjectToObjectDurationLinkedEffectUpdate(
    uint EffectId,
    uint Timestamp,
    uint SourceGameId,
    string SourceBoneChain,
    uint TargetGameId,
    short DurationLink,
    ushort EffectDefinitionId,
    Vector3 SpatialOffset,
    short EndpointShortA,
    short EndpointShortB,
    byte DescriptorFlag,
    float Scale,
    Vector3 HsvShift,
    float Speedup);

public sealed record Net7DecalEntry(uint Field0, uint Field1, byte[] Data);
public sealed record Net7DecalUpdate(uint GameId, IReadOnlyList<Net7DecalEntry> Decals);

public sealed record Net7ColorizationEntry(uint Field, byte[] Vector);
public sealed record Net7ColorizationUpdate(uint GameId, IReadOnlyList<Net7ColorizationEntry> Entries);

public sealed record Net7ZBandUpdate(byte[] FieldA, byte[] FieldB);
public sealed record Net7BoundingBoxUpdate(byte[] BlockA, byte[] BlockB);
public sealed record Net7InitializeRenderStateUpdate(uint GameId, uint RenderStateId, uint LinkId);
public sealed record Net7RenderStateActionUpdate(uint GameId, uint RenderStateId);
public sealed record Net7SplineUpdate(byte[] Data);
public sealed record Net7CreateAttachmentUpdate(uint ParentId, uint ChildId, uint AttachmentSelector);
public sealed record Net7ClientSoundUpdate(byte[] SoundNameBytes, string SoundName, uint Channel, byte Queue);
public sealed record Net7ClientSoundEvent(string? Name, uint Channel, byte Queue, uint? Command);
public sealed record Net7ClientSoundCommandUpdate(uint Command, uint Channel)
{
    public bool AppliesToCurrentSoundManager => Command == 2 || (Command < 2 && Channel <= 15);
}
public sealed record Net7NameDecalUpdate(uint GameId, string Name, byte[] RawName, Vector3 Rgb);
public sealed record Net7SubPartValue(string Name, uint PartValue);
public sealed record Net7SubPartsUpdate(uint GameId, IReadOnlyList<Net7SubPartValue> Parts);

/// <summary>
/// Current-client wire codecs for the 18 inbound
/// effects/render/attachments packet families. No historical Freya layouts are
/// used as authority here.
/// </summary>
public static class Net7EffectsRenderWire
{
    public const int PointEffectPayloadSize = 40;
    public const int DecalRecordSize = 24;
    public const int ColorizationEntrySize = 16;
    public const int ZBandPayloadSize = 8;
    public const int BoundingBoxPayloadSize = 16;
    public const int InitializeRenderStatePayloadSize = 12;
    public const int RenderStateActionPayloadSize = 8;
    public const int CreateAttachmentPayloadSize = 12;
    public const int ClientSoundCommandPayloadSize = 8;
    public const int NameDecalPayloadSize = 48;

    public static Net7PointEffectUpdate DecodePointEffect(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, PointEffectPayloadSize, "0x000A Point Effect");
        return new Net7PointEffectUpdate(
            ReadU32(payload, 0),
            ReadU32(payload, 4),
            ReadVector3(payload, 8),
            ReadI16(payload, 20),
            ReadU16(payload, 22),
            ReadF32(payload, 24),
            ReadVector3(payload, 28));
    }

    public static Net7ObjectToEffectEffectUpdate DecodeObjectToEffectEffect(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload, "0x000C Object To Effect Effect");
        uint effectId = reader.U32();
        uint timestamp = reader.U32();
        uint sourceId = reader.U32();
        string bone = reader.CString();
        uint targetId = reader.U32();
        short duration = reader.I16();
        ushort definition = reader.U16();
        Vector3 targetOffset = reader.Vector3();
        short auxiliaryA = reader.I16();
        short auxiliaryB = reader.I16();
        float scale = reader.F32();
        Vector3 hsv = reader.Vector3();
        float speedup = reader.F32();
        return new Net7ObjectToEffectEffectUpdate(
            effectId, timestamp, sourceId, bone, targetId, duration, definition,
            targetOffset, auxiliaryA, auxiliaryB, scale, hsv, speedup);
    }

    public static Net7ObjectToPointEffectUpdate DecodeObjectToPointEffect(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload, "0x000D Object To Point Effect");
        uint effectId = reader.U32();
        uint timestamp = reader.U32();
        uint sourceId = reader.U32();
        string bone = reader.CString();
        Vector3 targetPoint = reader.Vector3();
        short duration = reader.I16();
        ushort definition = reader.U16();
        float scale = reader.F32();
        Vector3 hsv = reader.Vector3();
        float speedup = reader.F32();
        return new Net7ObjectToPointEffectUpdate(
            effectId, timestamp, sourceId, bone, targetPoint, duration, definition,
            scale, hsv, speedup);
    }

    public static Net7ObjectToObjectDurationLinkedEffectUpdate DecodeObjectToObjectDurationLinkedEffect(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload, "0x000E Object To Object Duration Linked Effect");
        uint effectId = reader.U32();
        uint timestamp = reader.U32();
        uint sourceId = reader.U32();
        string bone = reader.CString();
        uint targetId = reader.U32();
        short durationLink = reader.I16();
        ushort definition = reader.U16();
        Vector3 spatialOffset = reader.Vector3();
        short endpointShortA = reader.I16();
        short endpointShortB = reader.I16();
        byte descriptorFlag = reader.U8();
        float scale = reader.F32();
        Vector3 hsv = reader.Vector3();
        float speedup = reader.F32();
        return new Net7ObjectToObjectDurationLinkedEffectUpdate(
            effectId, timestamp, sourceId, bone, targetId, durationLink,
            definition, spatialOffset, endpointShortA, endpointShortB, descriptorFlag,
            scale, hsv, speedup);
    }

    public static Net7DecalUpdate DecodeDecal(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
            throw new InvalidDataException("0x0010 Decal payload is shorter than GameID.");
        uint gameId = ReadU32(payload, 0);
        // 008807B0 initializes the signed count before its bounded read.
        int count = payload.Length >= 6 ? Math.Max(0, (int)ReadI16(payload, 4))
            : payload.Length == 5 ? payload[4] : 0;
        // Each iteration reads the 24-byte record into the same contiguous stack
        // locals (local_24/local_20/local_1c) with 004AACE0's bounded copy and
        // appends a copy, so a short record overlays the previous one and the
        // exhausted iterations append it again. The first record has no prior
        // value: reading it short exposes uninitialized storage (safety rule).
        if (count > 0 && payload.Length < 6 + DecalRecordSize)
            throw new InvalidDataException("0x0010 Decal first record is incomplete; its locals are uninitialized.");
        var entries = new Net7DecalEntry[count];
        Span<byte> record = stackalloc byte[DecalRecordSize];
        int offset = Math.Min(6, payload.Length);
        for (int i = 0; i < count; i++)
        {
            int available = Math.Min(DecalRecordSize, payload.Length - offset);
            payload.Slice(offset, available).CopyTo(record);
            offset += available;
            entries[i] = new Net7DecalEntry(
                ReadU32(record, 0),
                ReadU32(record, 4),
                record.Slice(8, 16).ToArray());
        }
        return new Net7DecalUpdate(gameId, entries);
    }

    public static Net7ColorizationUpdate DecodeColorization(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
            throw new InvalidDataException("0x0011 Colorization payload is shorter than GameID.");
        uint gameId = ReadU32(payload, 0);
        // 0087F510 zero-initializes the signed count before its bounded read and
        // loops only while it is positive; a nonpositive count is an empty update
        // whatever follows.
        short signedCount = payload.Length >= 6 ? ReadI16(payload, 4)
            : payload.Length == 5 ? payload[4] : (short)0;
        if (signedCount <= 0)
            return new Net7ColorizationUpdate(gameId, []);
        if (payload.Length < 6)
            throw new InvalidDataException("0x0011 Colorization first entry is absent; its locals are uninitialized.");
        // Each native record is two (u32, 12-byte) paint entries (0087F510/0087F730/
        // 0087F870: 32 bytes). Current Net7 counts the 16-byte halves, so retail reads
        // past the packet for the second half of its records.
        // Safety rule: entries beyond the payload are undefined native reads
        // (004AACE0 copies only available bytes); the complete entries are kept.
        int count = Math.Min(signedCount * 2, (payload.Length - 6) / ColorizationEntrySize);
        var entries = new Net7ColorizationEntry[count];
        int offset = 6;
        for (int i = 0; i < count; i++, offset += ColorizationEntrySize)
        {
            entries[i] = new Net7ColorizationEntry(
                ReadU32(payload, offset),
                payload.Slice(offset + 4, 12).ToArray());
        }
        return new Net7ColorizationUpdate(gameId, entries);
    }

    public static Net7ZBandUpdate DecodeZBand(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, ZBandPayloadSize, "0x002A Set ZBand");
        return new Net7ZBandUpdate(payload[..4].ToArray(), payload[4..8].ToArray());
    }

    public static Net7BoundingBoxUpdate DecodeBoundingBox(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, BoundingBoxPayloadSize, "0x002B Set BBox");
        return new Net7BoundingBoxUpdate(payload[..8].ToArray(), payload[8..16].ToArray());
    }

    public static Net7InitializeRenderStateUpdate DecodeInitializeRenderState(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, InitializeRenderStatePayloadSize, "0x002F Initialize Render State");
        return new Net7InitializeRenderStateUpdate(ReadU32(payload, 0), ReadU32(payload, 4), ReadU32(payload, 8));
    }

    public static Net7RenderStateActionUpdate DecodeRenderStateAction(ReadOnlySpan<byte> payload, ushort opcode)
    {
        if (opcode is not (0x0030 or 0x0031 or 0x0032))
            throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Render-state action decoder only owns 0x0030..0x0032.");
        RequirePrefix(payload, RenderStateActionPayloadSize, $"0x{opcode:X4} render-state action");
        return new Net7RenderStateActionUpdate(ReadU32(payload, 0), ReadU32(payload, 4));
    }

    public static Net7SplineUpdate DecodeSpline(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
            throw new InvalidDataException("0x0038 Spline payload is shorter than ByteCount.");
        uint count = ReadU32(payload, 0);
        if (count > int.MaxValue)
            throw new InvalidDataException("0x0038 Spline ByteCount exceeds the supported in-memory range.");
        int expected = checked(4 + (int)count);
        RequirePrefix(payload, expected, "0x0038 Spline");
        return new Net7SplineUpdate(payload.Slice(4, (int)count).ToArray());
    }

    public static Net7CreateAttachmentUpdate DecodeCreateAttachment(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, CreateAttachmentPayloadSize, "0x004A Create Attachment");
        return new Net7CreateAttachmentUpdate(
            BinaryPrimitives.ReadUInt32BigEndian(payload[..4]),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(8, 4)));
    }

    public static Net7ClientSoundUpdate DecodeClientSound(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 9)
            throw new InvalidDataException("0x006A Client Sound payload is shorter than its count plus Channel/Queue fields.");
        uint count = ReadU32(payload, 0);
        if (count > int.MaxValue)
            throw new InvalidDataException("0x006A Client Sound name byte count exceeds the supported in-memory range.");
        int nameLength = (int)count;
        int expected = checked(4 + nameLength + 5);
        RequirePrefix(payload, expected, "0x006A Client Sound");
        ReadOnlySpan<byte> nameBytes = payload.Slice(4, nameLength);
        int nul = nameBytes.IndexOf((byte)0);
        // Retail 008A5050 reads count bytes without checking for a terminator.
        // Preserve C-string truncation when present; never read beyond the count
        // or turn the native consumer's unbounded read into a wire rejection.
        if (nul < 0)
            nul = nameBytes.Length;
        return new Net7ClientSoundUpdate(
            nameBytes.ToArray(),
            Encoding.Latin1.GetString(nameBytes[..nul]),
            ReadU32(payload, 4 + nameLength),
            payload[8 + nameLength]);
    }

    public static Net7ClientSoundCommandUpdate DecodeClientSoundCommand(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, ClientSoundCommandPayloadSize, "0x006B Client Sound Command");
        uint command = ReadU32(payload, 0);
        // 007384A0 ignores commands outside 0..2.
        return new Net7ClientSoundCommandUpdate(command, ReadU32(payload, 4));
    }

    public static Net7NameDecalUpdate DecodeNameDecal(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, NameDecalPayloadSize, "0x00B2 Name Decal");
        byte[] rawName = payload.Slice(4, 32).ToArray();
        int nul = rawName.AsSpan().IndexOf((byte)0);
        if (nul < 0)
            nul = rawName.Length;
        return new Net7NameDecalUpdate(
            ReadU32(payload, 0),
            Encoding.Latin1.GetString(rawName.AsSpan(0, nul)),
            rawName,
            ReadVector3(payload, 36));
    }

    public static Net7SubPartsUpdate DecodeSubParts(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload, "0x00B4 Sub Parts");
        // 008756B0 initializes the ID to -1 and count/value to zero.
        uint gameId = reader.InitializedNetworkU32(uint.MaxValue);
        int count = Math.Max(0, unchecked((int)reader.InitializedNetworkU32()));
        var parts = new Net7SubPartValue[count];
        for (int i = 0; i < parts.Length; i++)
            parts[i] = new Net7SubPartValue(reader.CString(), reader.InitializedNetworkU32());
        return new Net7SubPartsUpdate(gameId, parts);
    }

    private static uint ReadU32(ReadOnlySpan<byte> payload, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));

    private static ushort ReadU16(ReadOnlySpan<byte> payload, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset, 2));

    private static short ReadI16(ReadOnlySpan<byte> payload, int offset)
        => BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(offset, 2));

    private static float ReadF32(ReadOnlySpan<byte> payload, int offset)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4)));

    private static Vector3 ReadVector3(ReadOnlySpan<byte> payload, int offset)
        => new(ReadF32(payload, offset), ReadF32(payload, offset + 4), ReadF32(payload, offset + 8));

    // 008834D0/008A5320 read their fields; their consumers do not inspect a tail.
    private static void RequirePrefix(ReadOnlySpan<byte> payload, int expected, string label)
    {
        if (payload.Length < expected)
            throw new InvalidDataException($"{label} payload needs {expected} bytes; received {payload.Length}.");
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _payload;
        private readonly string _label;
        private int _offset;

        public Reader(ReadOnlySpan<byte> payload, string label)
        {
            _payload = payload;
            _label = label;
            _offset = 0;
        }

        public byte U8()
        {
            Ensure(1);
            return _payload[_offset++];
        }

        public ushort U16()
        {
            Ensure(2);
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_payload.Slice(_offset, 2));
            _offset += 2;
            return value;
        }

        public short I16()
        {
            Ensure(2);
            short value = BinaryPrimitives.ReadInt16LittleEndian(_payload.Slice(_offset, 2));
            _offset += 2;
            return value;
        }

        public uint U32()
        {
            Ensure(4);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_payload.Slice(_offset, 4));
            _offset += 4;
            return value;
        }

        public uint InitializedNetworkU32(uint initial = 0)
        {
            Span<byte> value = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(value, initial);
            int count = Math.Min(4, _payload.Length - _offset);
            _payload.Slice(_offset, count).CopyTo(value);
            _offset += count;
            return BinaryPrimitives.ReadUInt32BigEndian(value);
        }

        public float F32()
        {
            Ensure(4);
            float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(_payload.Slice(_offset, 4)));
            _offset += 4;
            return value;
        }

        public Vector3 Vector3() => new(F32(), F32(), F32());

        public string CString()
        {
            ReadOnlySpan<byte> remaining = _payload[_offset..];
            int nul = remaining.IndexOf((byte)0);
            int length = nul < 0 ? remaining.Length : nul;
            string value = Encoding.Latin1.GetString(remaining[..length]);
            _offset += length + (nul < 0 ? 0 : 1);
            return value;
        }

        private void Ensure(int bytes)
        {
            if (_offset + bytes > _payload.Length)
                throw new InvalidDataException($"{_label} is truncated at byte {_offset}; needs {bytes} more byte(s).");
        }
    }
}
