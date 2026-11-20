using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Net7.Protocol;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.World;

public sealed record Net7CreateUpdate(
    uint GameId,
    float Scale,
    short BaseAsset,
    byte ObjectType,
    float HsvH,
    float HsvS,
    float HsvV);

public sealed record Net7SimplePositionUpdate(
    uint GameId,
    uint ServerTimestamp,
    Vector3 Position,
    Quaternion Orientation,
    Vector3 Velocity);

public sealed record Net7AdvancedPositionUpdate(
    ushort OptionalMask,
    uint GameId,
    uint ServerTimestamp,
    Vector3 Position,
    Quaternion Orientation,
    uint MovementId,
    float CurrentSpeed,
    float SetSpeed,
    float Acceleration,
    float RotY,
    float DesiredY,
    float RotZ,
    float DesiredZ,
    Vector3 ImpartedVelocity,
    float ImpartedSpin,
    float ImpartedRoll,
    float ImpartedPitch,
    uint UpdatePeriod);

public sealed record Net7PlanetPositionUpdate(
    uint GameId,
    uint ServerTimestamp,
    Vector3 SpatialVector,
    uint OrbitId,
    float OrbitDistance,
    float OrbitAngle,
    float OrbitRate,
    float RotationAngle,
    float RotationRate,
    float TiltAngle);

public sealed record Net7FixedPositionUpdate(uint GameId, Vector3 Position, Quaternion Orientation);

public sealed record Net7FormationPositionUpdate(uint GameId, uint LeaderId, Vector3 Offset);

public sealed record Net7ComponentPositionUpdate(
    uint GameId,
    uint ServerTimestamp,
    Vector3 Position,
    Quaternion Orientation,
    Vector3 Velocity,
    float ImpartedDecay,
    float TractorSpeed,
    uint TractorId,
    uint TractorEffectId);

public enum Net7Reaction : uint
{
    Attack = 0,
    Shun = 1,
    Friendly = 2,
    Adoration = 3,
}

public sealed record Net7RelationshipUpdate(uint ObjectId, uint ReactionRaw, byte IsAttacking)
{
    public Net7Reaction? DefinedReaction => ReactionRaw <= 3 ? (Net7Reaction)ReactionRaw : null;
}

public sealed record Net7ReactionEntry(uint ObjectId, uint ReactionRaw);
/// <summary>TrailingRepeats counts further entries after Entries once the payload is exhausted.
/// 00873F70 then reads nothing: the reaction local is retained and 0086EDF0's ntohl swaps the
/// retained id on every read, so the tail alternates Entries[^2], Entries[^1], Entries[^2], ...</summary>
public sealed record Net7ReactionBatch(IReadOnlyList<Net7ReactionEntry> Entries, uint TrailingRepeats = 0);
public sealed record Net7AttackerBatch(byte IsAttacking, IReadOnlyList<uint> ObjectIds, uint TrailingRepeats = 0);
public sealed record Net7SetTargetUpdate(uint TargetId, uint EndpointObjectId);

public sealed record Net7NavigationUpdate(
    uint GameId,
    float Signature,
    byte PlayerHasVisited,
    uint NavType,
    byte IsHuge);

public sealed record Net7AuxDataEnvelope(ushort Opcode, uint Target, byte[] Delta)
{
    // Preserved separately to identify the known ignored Ship engine tail.
    // Delta itself remains the ordinary declared view.
    public byte[] TrailingBytes { get; init; } = [];
}

public sealed record Net7ServerHandoff(
    Net7SectorJoinClientData NextJoinData,
    string SourceSectorName,
    string SourceSystemName,
    string DestinationSectorName,
    string DestinationSystemName);

public sealed record Net7AdvancedObjectA(
    uint GameId,
    byte ObjectType,
    ushort BaseAsset,
    float Scale,
    float HsvH,
    float HsvS,
    float HsvV,
    byte Reaction,
    byte IgnoredByte18,
    Vector3 Position,
    Quaternion Orientation,
    float SharedNavigationRadarScalar,
    byte Flags,
    byte[] NameBytes)
{
    public string EffectiveName => DecodeEffectiveCString(NameBytes);

    private static string DecodeEffectiveCString(byte[] bytes)
    {
        int length = Array.IndexOf(bytes, (byte)0);
        if (length < 0)
            length = bytes.Length;
        return Encoding.Latin1.GetString(bytes, 0, length);
    }
}

public sealed record Net7AdvancedObjectB(
    uint GameId,
    ushort BaseAsset,
    float Scale,
    float HsvH,
    float HsvS,
    Vector3 Position,
    Quaternion Orientation,
    byte[] NameBytes)
{
    public string EffectiveName
    {
        get
        {
            int length = Array.IndexOf(NameBytes, (byte)0);
            if (length < 0)
                length = NameBytes.Length;
            return Encoding.Latin1.GetString(NameBytes, 0, length);
        }
    }
}

public static class Net7WorldWire
{
    public static Net7CreateUpdate DecodeCreate(ReadOnlySpan<byte> payload)
    {
        var c = new Cursor(payload);
        Net7CreateUpdate result = new(
            c.ReadUInt32Little(),
            c.ReadSingleRaw(),
            c.ReadInt16Little(),
            c.ReadByte(),
            c.ReadSingleRaw(),
            c.ReadSingleRaw(),
            c.ReadSingleRaw());
        // 0087FA70 reads the defined prefix without requiring exhaustion.
        return result;
    }

    public static uint DecodeRemove(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
            throw new InvalidDataException($"0x0007 Remove requires 4 payload bytes, got {payload.Length}.");
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    public static Net7SetTargetUpdate DecodeSetTarget(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
            throw new InvalidDataException($"0x0019 Set Target requires 8 payload bytes, got {payload.Length}.");
        return new Net7SetTargetUpdate(
            BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)));
    }

    public static Net7SimplePositionUpdate DecodeSimplePosition(ReadOnlySpan<byte> payload)
    {
        var c = new Cursor(payload);
        Net7SimplePositionUpdate result = new(
            c.ReadUInt32Little(),
            c.ReadUInt32Little(),
            c.ReadVector3(),
            c.ReadQuaternion(),
            c.ReadVector3());
        // 008AC1B0 consumes its fields and ignores unused packet bytes.
        return result;
    }

    public static Net7AdvancedPositionUpdate DecodeAdvancedPosition(ReadOnlySpan<byte> payload)
    {
        var c = new MovementCursor(payload);
        ushort mask = c.ReadUInt16Little();
        // 008ACAB0 tests defined bits only; unused mask bits are ignored.

        uint gameId = c.ReadUInt32Little();
        uint timestamp = c.ReadUInt32Little();
        Vector3 position = c.ReadVector3();
        Quaternion orientation = c.ReadQuaternion();
        uint movementId = c.ReadUInt32Little();

        float currentSpeed = (mask & (1 << 0)) != 0 ? c.ReadSingleRaw() : 0f;
        float setSpeed = (mask & (1 << 1)) != 0 ? c.ReadSingleRaw() : currentSpeed;
        float acceleration = (mask & (1 << 2)) != 0 ? c.ReadSingleRaw() : 0f;
        float rotY = (mask & (1 << 3)) != 0 ? c.ReadSingleRaw() : 0f;
        float desiredY = (mask & (1 << 4)) != 0
            ? c.ReadSingleRaw()
            : (rotY > 0 ? MathF.PI : -MathF.PI);
        float rotZ = (mask & (1 << 5)) != 0 ? c.ReadSingleRaw() : 0f;
        float desiredZ = (mask & (1 << 6)) != 0
            ? c.ReadSingleRaw()
            : (rotZ > 0 ? 1000f : -1000f);

        Vector3 impartedVelocity = Vector3.Zero;
        float impartedSpin = 0f;
        float impartedRoll = 0f;
        float impartedPitch = 0f;
        if ((mask & (1 << 7)) != 0)
        {
            impartedVelocity = c.ReadVector3();
            impartedSpin = c.ReadSingleRaw();
            impartedRoll = c.ReadSingleRaw();
            impartedPitch = c.ReadSingleRaw();
        }

        uint updatePeriod = (mask & (1 << 8)) != 0 ? c.ReadUInt32Little(500u) : 500u;
        // 008696C0 disposes the payload view without an exhaustion check.
        return new Net7AdvancedPositionUpdate(
            mask, gameId, timestamp, position, orientation, movementId,
            currentSpeed, setSpeed, acceleration, rotY, desiredY, rotZ, desiredZ,
            impartedVelocity, impartedSpin, impartedRoll, impartedPitch, updatePeriod);
    }

    public static Net7PlanetPositionUpdate DecodePlanetPosition(ReadOnlySpan<byte> payload)
    {
        var c = new Cursor(payload);
        Net7PlanetPositionUpdate result = new(
            c.ReadUInt32Little(),
            c.ReadUInt32Little(),
            c.ReadVector3Raw(),
            c.ReadUInt32Little(),
            c.ReadSingleRaw(),
            c.ReadSingleRaw(),
            c.ReadSingleRaw(),
            c.ReadSingleRaw(),
            c.ReadSingleRaw(),
            c.ReadSingleRaw());
        // 008ADDF0 consumes its fields and ignores unused packet bytes.
        return result;
    }

    public static Net7FixedPositionUpdate DecodeFixedPosition(ReadOnlySpan<byte> payload)
    {
        var c = new Cursor(payload);
        Net7FixedPositionUpdate result = new(c.ReadUInt32Little(), c.ReadVector3Raw(), c.ReadQuaternionRaw());
        // 008ABBF0 consumes its fields and ignores unused packet bytes.
        return result;
    }

    public static Net7FormationPositionUpdate DecodeFormationPosition(ReadOnlySpan<byte> payload)
    {
        // 008AE5C0 initializes both IDs and the offset to zero. 004AACE0
        // copies an available prefix into those fields without rejecting it.
        var c = new MovementCursor(payload);
        Net7FormationPositionUpdate result = new(
            c.ReadUInt32Little(), c.ReadUInt32Little(), c.ReadVector3());
        // 008AE5C0 consumes its fields and ignores unused packet bytes.
        return result;
    }

    public static Net7ComponentPositionUpdate DecodeComponentPosition(ReadOnlySpan<byte> payload)
    {
        var c = new Cursor(payload);
        Net7ComponentPositionUpdate result = new(
            c.ReadUInt32Little(),
            c.ReadUInt32Little(),
            c.ReadVector3Raw(),
            c.ReadQuaternionRaw(),
            c.ReadVector3Raw(),
            c.ReadSingleRaw(),
            c.ReadSingleRaw(),
            c.ReadUInt32Little(),
            c.ReadUInt32Little());
        // 008AE240 consumes its fields and ignores unused packet bytes.
        return result;
    }

    public static Net7RelationshipUpdate DecodeRelationship(ReadOnlySpan<byte> payload)
    {
        // 00873C30 / 00848C80 retain initialized ID, reaction and attacking
        // bytes on short reads; 0086EDF0 swaps even a partial network-order ID.
        var c = new MovementCursor(payload);
        Net7RelationshipUpdate result = new(c.ReadUInt32Big(uint.MaxValue), c.ReadUInt32Little(1), c.ReadByte());
        // 00873C30 leaves unused packet bytes unread.
        return result;
    }

    public static Net7ReactionBatch DecodeReactionBatch(ReadOnlySpan<byte> payload)
    {
        // 00873F70 retains the previous entry's initialized bytes on short reads.
        // 0086EDF0 swaps the object id after every read, including an empty read.
        var c = new MovementCursor(payload);
        uint count = c.ReadUInt32Big();
        uint objectId = 0, reaction = 1;
        List<Net7ReactionEntry> entries = new();
        for (uint i = 0; i < count; i++)
        {
            if (c.Remaining == 0)
            {
                // Every remaining iteration reads nothing: the tail is a two-entry
                // cycle. Keep it as a count, not count-many allocations (an
                // unbounded count exhausts retail's own vector growth).
                uint left = count - i;
                for (uint k = 0; k < Math.Min(left, 2u); k++)
                {
                    objectId = BinaryPrimitives.ReverseEndianness(objectId);
                    entries.Add(new Net7ReactionEntry(objectId, reaction));
                }
                return new Net7ReactionBatch(entries, left - Math.Min(left, 2u));
            }
            objectId = c.ReadUInt32Big(objectId);
            reaction = c.ReadUInt32Little(reaction);
            entries.Add(new Net7ReactionEntry(objectId, reaction));
        }
        return new Net7ReactionBatch(entries);
    }

    public static Net7AttackerBatch DecodeAttackerBatch(ReadOnlySpan<byte> payload)
    {
        // 00874BE0 uses the same persistent network-order id local as 00873F70.
        var c = new MovementCursor(payload);
        byte attacking = c.ReadByte();
        uint count = c.ReadUInt32Big();
        uint objectId = 0;
        List<uint> ids = new();
        for (uint i = 0; i < count; i++)
        {
            if (c.Remaining == 0)
            {
                uint left = count - i;
                for (uint k = 0; k < Math.Min(left, 2u); k++)
                {
                    objectId = BinaryPrimitives.ReverseEndianness(objectId);
                    ids.Add(objectId);
                }
                return new Net7AttackerBatch(attacking, ids, left - Math.Min(left, 2u));
            }
            objectId = c.ReadUInt32Big(objectId);
            ids.Add(objectId);
        }
        return new Net7AttackerBatch(attacking, ids);
    }

    public static Net7NavigationUpdate DecodeNavigation(ReadOnlySpan<byte> payload)
    {
        // 0089E9F0 initializes these fields; 004AACE0 retains untouched default
        // bytes on a short read. 0073A600 applies the decoded packet without an
        // exhaustion check.
        var c = new MovementCursor(payload);
        return new Net7NavigationUpdate(
            c.ReadUInt32Little(), c.ReadSingleRaw(), c.ReadByte(), c.ReadUInt32Little(uint.MaxValue), c.ReadByte());
    }

    public static uint DecodeNavigationDelete(ReadOnlySpan<byte> payload)
    {
        // 0089ED50 starts with id zero and reads the available prefix only.
        // 0073A710 removes navigation from that object if it exists.
        var c = new MovementCursor(payload);
        return c.ReadUInt32Little();
    }

    public static Net7AuxDataEnvelope DecodeAuxDataEnvelope(ushort opcode, ReadOnlySpan<byte> payload)
    {
        if (opcode is not 0x001B and not 0x001C)
            throw new ArgumentOutOfRangeException(nameof(opcode), $"0x{opcode:X4} is not an AuxData envelope opcode.");
        var c = new Cursor(payload);
        uint target = c.ReadUInt32Little();
        ushort length = c.ReadUInt16Little();
        if ((length & 0x8000) != 0)
            throw new InvalidDataException($"0x{opcode:X4} AuxData delta length sets the unsupported signed-length bit.");
        // Live thrust receipt reopens the blanket overrun rejection.
        // Retail 00876E90 ignores the short-read count from 004AACE0, which
        // copies min(requested, remaining). Its self-delimiting AuxData tree
        // can finish without touching the uninitialized allocation tail.
        // Preserve only the actual bytes; never pad/fabricate that unsafe tail.
        // The incoming visitor retains completed fields and stops on a short read.
        byte[] delta = c.ReadBytes(Math.Min(length, c.Remaining));
        // When enough bytes exist, the constructor reads the declared
        // delta bytes and does not validate that the packet decode context has
        // been exhausted. Preserve that accepted-input behavior: locally
        // serialized frames are exact, but trailing packet bytes do not become
        // part of this declared Delta and must not make the envelope fail.
        // The object decoder may inspect the known eight-byte Ship engine
        // suffix to explain a tolerated partial update. It never applies it.
        return new Net7AuxDataEnvelope(opcode, target, delta) { TrailingBytes = c.ReadBytes(c.Remaining) };
    }

    public static Net7ServerHandoff DecodeServerHandoff(ReadOnlySpan<byte> payload)
    {
        var c = new Cursor(payload);
        Net7SectorJoinClientData join = Net7SectorJoinClientData.FromWireBytes(c.ReadSpan(Net7SectorJoinClientData.WireSize));
        string sourceSector = c.ReadSharedString();
        string sourceSystem = c.ReadSharedString();
        string destinationSector = c.ReadSharedString();
        string destinationSystem = c.ReadSharedString();
        // 008946B0/00737CB0 consume the fields without requiring packet exhaustion.
        return new Net7ServerHandoff(join, sourceSector, sourceSystem, destinationSector, destinationSystem);
    }

    public static Net7AdvancedObjectA DecodeAdvancedObjectA(ReadOnlySpan<byte> payload)
    {
        // Proxy 0040F3C0/0040F600 forward floats without finite-value admission.
        var c = new Cursor(payload);
        uint gameId = c.ReadUInt32Little();
        byte type = c.ReadByte();
        ushort baseAsset = c.ReadUInt16Little();
        float scale = c.ReadSingleRaw();
        float h = c.ReadSingleRaw();
        float s = c.ReadSingleRaw();
        float v = c.ReadSingleRaw();
        byte reaction = c.ReadByte();
        byte ignored = c.ReadByte();
        Vector3 position = c.ReadVector3Raw();
        Quaternion orientation = c.ReadQuaternionRaw();
        float sharedScalar = c.ReadSingleRaw();
        byte flags = c.ReadByte();
        ushort nameLength = c.ReadUInt16Little();
        byte[] name = c.ReadBytes(nameLength);
        return new Net7AdvancedObjectA(
            gameId, type, baseAsset, scale, h, s, v, reaction, ignored,
            position, orientation, sharedScalar, flags, name);
    }

    public static Net7AdvancedObjectB DecodeAdvancedObjectB(ReadOnlySpan<byte> payload)
    {
        var c = new Cursor(payload);
        uint gameId = c.ReadUInt32Little();
        ushort baseAsset = c.ReadUInt16Little();
        float scale = c.ReadSingleRaw();
        float h = c.ReadSingleRaw();
        float s = c.ReadSingleRaw();
        Vector3 position = c.ReadVector3Raw();
        Quaternion orientation = c.ReadQuaternionRaw();
        ushort nameLength = c.ReadUInt16Little();
        byte[] name = c.ReadBytes(nameLength);
        return new Net7AdvancedObjectB(gameId, baseAsset, scale, h, s, position, orientation, name);
    }

    // 008ACAB0 initializes packet fields before 004AACE0 copies available bytes.
    // Short reads retain the untouched bytes of those defaults, not an all-or-nothing read.
    private ref struct MovementCursor(ReadOnlySpan<byte> source)
    {
        private ReadOnlySpan<byte> _remaining = source;
        public int Remaining => _remaining.Length;
        private uint Read(int width, uint initial)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, initial);
            int count = Math.Min(width, _remaining.Length);
            _remaining[..count].CopyTo(bytes);
            _remaining = _remaining[count..];
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
        public byte ReadByte() => (byte)Read(1, 0);
        public ushort ReadUInt16Little() => (ushort)Read(2, 0);
        public uint ReadUInt32Little(uint initial = 0) => Read(4, initial);
        public uint ReadUInt32Big(uint initial = 0) => BinaryPrimitives.ReverseEndianness(Read(4, initial));
        public float ReadSingleRaw() => BitConverter.UInt32BitsToSingle(Read(4, 0));
        public Vector3 ReadVector3() => new(ReadSingleRaw(), ReadSingleRaw(), ReadSingleRaw());
        public Quaternion ReadQuaternion() => new(ReadSingleRaw(), ReadSingleRaw(), ReadSingleRaw(), ReadSingleRaw());
    }

    private ref struct Cursor
    {
        private ReadOnlySpan<byte> _remaining;

        public Cursor(ReadOnlySpan<byte> source) => _remaining = source;
        public int Remaining => _remaining.Length;

        public ReadOnlySpan<byte> ReadSpan(int count)
        {
            Require(count);
            ReadOnlySpan<byte> result = _remaining[..count];
            _remaining = _remaining[count..];
            return result;
        }

        public byte[] ReadBytes(int count) => ReadSpan(count).ToArray();

        public byte ReadByte()
        {
            Require(1);
            byte result = _remaining[0];
            _remaining = _remaining[1..];
            return result;
        }

        public short ReadInt16Little()
        {
            short result = BinaryPrimitives.ReadInt16LittleEndian(ReadSpan(2));
            return result;
        }

        public ushort ReadUInt16Little() => BinaryPrimitives.ReadUInt16LittleEndian(ReadSpan(2));
        public uint ReadUInt32Little() => BinaryPrimitives.ReadUInt32LittleEndian(ReadSpan(4));
        public uint ReadUInt32Big() => BinaryPrimitives.ReadUInt32BigEndian(ReadSpan(4));

        public float ReadSingleRaw()
            => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(ReadSpan(4)));

        public float ReadFiniteSingle()
        {
            float value = ReadSingleRaw();
            if (!float.IsFinite(value))
                throw new InvalidDataException("World packet contains a non-finite float.");
            return value;
        }

        public Vector3 ReadVector3Raw() => new(ReadSingleRaw(), ReadSingleRaw(), ReadSingleRaw());
        public Quaternion ReadQuaternionRaw() => new(ReadSingleRaw(), ReadSingleRaw(), ReadSingleRaw(), ReadSingleRaw());
        public Vector3 ReadVector3() => new(ReadFiniteSingle(), ReadFiniteSingle(), ReadFiniteSingle());
        public Quaternion ReadQuaternion() => new(ReadFiniteSingle(), ReadFiniteSingle(), ReadFiniteSingle(), ReadFiniteSingle());

        public string ReadSharedString()
        {
            ushort length = ReadUInt16Little();
            return Encoding.Latin1.GetString(ReadSpan(length));
        }

        private void Require(int count)
        {
            if (count < 0 || count > _remaining.Length)
                throw new InvalidDataException($"World packet is truncated; need {count} byte(s), have {_remaining.Length}.");
        }
    }
}
