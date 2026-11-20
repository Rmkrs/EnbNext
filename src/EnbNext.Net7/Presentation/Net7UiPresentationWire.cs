using System.Buffers.Binary;
using System.Text;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.Presentation;

public enum Net7MessageLineKind
{
    Priority,
    Queue,
    Push,
}

public sealed record Net7MessageLineUpdate(
    Net7MessageLineKind Kind,
    string Text,
    string EphemeralType,
    uint TimeMilliseconds,
    uint DisplayPriority);

public sealed record Net7SuperLineUpdate(string Text);
public sealed record Net7MessageTimeUpdate(byte Color, uint Time);
public sealed record Net7StarbaseSetUpdate(uint StarbaseId, byte Action, byte ExitMode);
public sealed record Net7StarbaseTowOption(uint StarbaseId, uint TowCost);
public sealed record Net7StarbaseTowUpdate(IReadOnlyList<Net7StarbaseTowOption> Options);
public sealed record Net7LoungeRoom(uint RoomType, uint Flags, IReadOnlyList<uint> Parameters)
{
    public IReadOnlyList<bool> CapabilityBits =>
    [
        (Flags & 0x001) != 0,
        (Flags & 0x002) != 0,
        (Flags & 0x004) != 0,
        (Flags & 0x008) != 0,
        (Flags & 0x010) != 0,
        (Flags & 0x020) != 0,
        (Flags & 0x040) != 0,
        (Flags & 0x080) != 0,
        (Flags & 0x100) != 0,
    ];
}
public sealed record Net7LoungeFacility(uint RoomType, uint Slot, uint FacilityType, uint FourthField);
public sealed record Net7LoungeNpc(
    uint RoomType,
    uint Slot,
    uint Field2,
    uint RoleCode,
    uint Variant,
    byte[] NameBytes,
    string Name,
    Net7AvatarData Avatar);
public sealed record Net7LoungeDefinitionUpdate(
    uint StarbaseDefinitionValue,
    IReadOnlyList<Net7LoungeRoom> Rooms,
    IReadOnlyList<Net7LoungeFacility> Facilities,
    IReadOnlyList<Net7LoungeNpc> Npcs);
public sealed record Net7TalkTreeResponse(uint ResponseActionOrId, string Text);
public sealed record Net7TalkTreeUpdate(string NpcSay, string NpcSoundFileName, IReadOnlyList<Net7TalkTreeResponse> Responses);
public sealed record Net7TalkTreeActionUpdate(uint Action)
{
    // 0064D230: unknown positive commands become -30; negative commands
    // retain their value. Both are consumed by the same close path.
    public bool EndsConversation => Action>7;
}
public sealed record Net7VerbListUpdate(uint GameId, IReadOnlyList<uint> Verbs);
public sealed record Net7VerbUpdate(uint GameId, IReadOnlyList<uint> Drop, IReadOnlyList<uint> Add);
public sealed record Net7DialogUpdate(ushort ByteLength, uint Type, byte[] Data);
public sealed record Net7OpenInterfaceUpdate(uint UiChange, uint UiType);
public sealed record Net7GadgetControlUpdate(ushort NameSize, uint Function, byte[] NameBytes, string Name);
public sealed record Net7ClickIndicatorUpdate(byte TurnOn, uint GameId)
{
    public bool IsOn => TurnOn != 0;
}
public sealed record Net7StarbaseRoomUpdate(uint AvatarId, uint NewRoom, uint OldRoom);
public sealed record Net7ConfirmedActionOffer(uint Type, uint Id, string Message);
public sealed record Net7ConfirmedActionTimeout(uint Type, uint Id);
public sealed record Net7ConfirmedActionPending(uint Type, uint Id, string Message)
{
    public bool UsesConfirmationDialog => Type == 0;
    public bool UsesChatUiRoute => Type == 1;
}

/// <summary>
/// Current-client codecs for the 20 message/dialog/starbase UI
/// inbound-only packet families. Raw little-endian fields remain little-endian;
/// only fields explicitly proven network-order are converted here. Retail packet
/// constructors consume their fields without requiring the containing frame to end:
/// 0089D8A0, 0089DEB0, 008A3090, 008A3370, 008978D0, 008A7430,
/// 008AF150, 008AFD10, 0089E6A0, 00881410, 0089F290, 00887A20,
/// 0087E870, 008A4020, 00870650 and 008709E0.
/// </summary>
public static class Net7UiPresentationWire
{
    public const int MessageLineFixedTailSize = 8;
    public const int StarbaseSetPayloadSize = 6;
    public const int StarbaseTowPayloadSize = 24;
    public const int TalkTreeActionPayloadSize = 4;
    public const int MessageTimePayloadSize = 5;
    public const int OpenInterfacePayloadSize = 8;
    public const int ClickIndicatorPayloadSize = 5;
    public const int StarbaseRoomUpdatePayloadSize = 12;
    public const int ConfirmedActionTimeoutPayloadSize = 8;

    public static Net7MessageLineUpdate DecodeMessageLine(ReadOnlySpan<byte> payload, ushort opcode)
    {
        Net7MessageLineKind kind = opcode switch
        {
            0x0020 => Net7MessageLineKind.Priority,
            0x0021 => Net7MessageLineKind.Queue,
            0x0022 => Net7MessageLineKind.Push,
            _ => throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Message-line decoder only owns 0x0020..0x0022."),
        };
        var reader = new Reader(payload, $"0x{opcode:X4} message line");
        string text = reader.CString();
        string ephemeralType = reader.CString();
        // 0089D8A0 initializes both fields before the bounded reads.
        uint time = reader.InitializedU32();
        uint priority = reader.InitializedU32();
        return new Net7MessageLineUpdate(kind, text, ephemeralType, time, priority);
    }

    public static Net7SuperLineUpdate DecodeSuperLine(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload, "0x0023 Super Line");
        string text = reader.CString();
        return new Net7SuperLineUpdate(text);
    }

    public static void DecodeClearMessageLine(ReadOnlySpan<byte> payload)
        => RequirePrefix(payload, 0, "0x0024 Clear Message Line");

    public static Net7StarbaseSetUpdate DecodeStarbaseSet(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, StarbaseSetPayloadSize, "0x004F Starbase Set");
        return new Net7StarbaseSetUpdate(ReadU32(payload, 0), payload[4], payload[5]);
    }

    public static Net7StarbaseTowUpdate DecodeStarbaseTow(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, StarbaseTowPayloadSize, "0x0050 Starbase Tow");
        uint id1 = ReadU32(payload, 0);
        uint id2 = ReadU32(payload, 4);
        uint id3 = ReadU32(payload, 8);
        uint cost1 = ReadU32(payload, 12);
        uint cost2 = ReadU32(payload, 16);
        uint cost3 = ReadU32(payload, 20);
        return new Net7StarbaseTowUpdate([
            new Net7StarbaseTowOption(id1, cost1),
            new Net7StarbaseTowOption(id2, cost2),
            new Net7StarbaseTowOption(id3, cost3),
        ]);
    }

    public static Net7LoungeDefinitionUpdate DecodeLoungeNpc(ReadOnlySpan<byte> payload)
    {
        // 008978D0: StarbaseDefinitionValue is read into uninitialized packet
        // storage (mandatory). All three counts are read through one stack local
        // initialized to zero, so an absent count keeps the previous count. Each
        // record is a copy of a template whose leading fields are uninitialized:
        // the first short record read is undefined. Safety rule: stop there and
        // keep the sections and complete records decoded before it.
        var reader = new Reader(payload, "0x0052 Lounge NPC");
        uint starbaseDefinitionValue = reader.U32();
        var rooms = new List<Net7LoungeRoom>();
        var facilities = new List<Net7LoungeFacility>();
        var npcs = new List<Net7LoungeNpc>();
        // A count above int.MaxValue makes the native vector resize fail.
        uint count = reader.OverlayU32(0);
        if (count > int.MaxValue) goto Done;
        for (int i = 0; i < (int)count; i++)
        {
            if (reader.Remaining < 28) goto Done;
            uint roomType = reader.U32();
            uint flags = reader.U32();
            var parameters = new uint[5];
            for (int p = 0; p < parameters.Length; p++)
                parameters[p] = reader.U32();
            rooms.Add(new Net7LoungeRoom(roomType, flags, parameters));
        }

        count = reader.OverlayU32(count);
        if (count > int.MaxValue) goto Done;
        for (int i = 0; i < (int)count; i++)
        {
            if (reader.Remaining < 16) goto Done;
            facilities.Add(new Net7LoungeFacility(reader.U32(), reader.U32(), reader.U32(), reader.U32()));
        }

        count = reader.OverlayU32(count);
        if (count > int.MaxValue) goto Done;
        for (int i = 0; i < (int)count; i++)
        {
            if (reader.Remaining < 24) goto Done;
            uint roomType = reader.U32();
            uint slot = reader.U32();
            uint field2 = reader.U32();
            uint roleCode = reader.U32();
            uint variant = reader.U32();
            uint nameByteCount = reader.U32();
            if (nameByteCount > (uint)reader.Remaining) goto Done;
            byte[] nameBytes = reader.Bytes(nameByteCount, $"NPC {i} name");
            string name = DecodeCountedLatin1(nameBytes);
            if (reader.Remaining <= Net7AvatarRosterCodec.AvatarSkeletonOffset) goto Done;
            (Net7AvatarData avatar, int consumed) = Net7AvatarRosterCodec.DecodeAvatarData(reader.RemainingSpan, $"0x0052 NPC {i} AvatarData");
            reader.Advance(consumed, $"NPC {i} AvatarData");
            npcs.Add(new Net7LoungeNpc(roomType, slot, field2, roleCode, variant, nameBytes, name, avatar));
        }

    Done:
        return new Net7LoungeDefinitionUpdate(starbaseDefinitionValue, rooms, facilities, npcs);
    }

    public static Net7TalkTreeUpdate DecodeTalkTree(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload, "0x0054 Talk Tree");
        string npcSay = reader.CString();
        string sound = reader.CString();
        // 008A6CA0 reads the count into an uninitialized signed char (mandatory)
        // and loops while it is positive. Each response ID is a bounded read
        // over one stack local initialized to zero, so an absent ID keeps the
        // previous ID; the response text (004AAE90) is defined at the end.
        int responseCount = unchecked((sbyte)reader.U8());
        var responses = new List<Net7TalkTreeResponse>(Math.Max(0, responseCount));
        uint responseId = 0;
        for (int i = 0; i < responseCount; i++)
        {
            responseId = reader.OverlayU32(responseId);
            responses.Add(new Net7TalkTreeResponse(responseId, reader.CString()));
        }
        // Retail 008A6CA0 stops after the counted responses. The containing
        // frame owns its length; trailing bytes are not another response.
        // Current rescue 0054 includes an extra NUL after its last string.
        return new Net7TalkTreeUpdate(npcSay, sound, responses);
    }

    public static Net7TalkTreeActionUpdate DecodeTalkTreeAction(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, TalkTreeActionPayloadSize, "0x0056 Talk Tree Action");
        return new Net7TalkTreeActionUpdate(ReadU32(payload, 0));
    }

    public static Net7VerbListUpdate DecodeVerbList(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload, "0x005B Verb List");
        uint gameId = reader.NetworkU32();
        uint? retained = null;
        uint[] verbs = reader.RetainedVerbRecords(reader.NetworkSignedCount(), ref retained, "verb");
        return new Net7VerbListUpdate(gameId, verbs);
    }

    public static Net7VerbUpdate DecodeVerbUpdate(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload, "0x005C Verb Update");
        uint gameId = reader.NetworkU32();
        // 008AFD10 reads both lists through the same record local (local_1c):
        // the add list's first record retains the drop list's last value.
        uint? retained = null;
        uint[] drop = reader.RetainedVerbRecords(reader.NetworkSignedCount(), ref retained, "drop verb");
        uint[] add = reader.RetainedVerbRecords(reader.NetworkSignedCount(), ref retained, "add verb");
        return new Net7VerbUpdate(gameId, drop, add);
    }

    public static Net7MessageTimeUpdate DecodeMessageTime(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, MessageTimePayloadSize, "0x0060 Message Time");
        return new Net7MessageTimeUpdate(payload[0], ReadU32(payload, 1));
    }

    public static Net7DialogUpdate DecodeDialog(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 6)
            throw new InvalidDataException("0x0062 Dialog payload is shorter than ByteLength + Type.");
        ushort byteLength = BinaryPrimitives.ReadUInt16LittleEndian(payload[..2]);
        int expected = checked(6 + byteLength);
        RequirePrefix(payload, expected, "0x0062 Dialog");
        return new Net7DialogUpdate(byteLength, ReadU32(payload, 2), payload.Slice(6, byteLength).ToArray());
    }

    public static Net7OpenInterfaceUpdate DecodeOpenInterface(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, OpenInterfacePayloadSize, "0x0066 Open Interface");
        return new Net7OpenInterfaceUpdate(ReadU32(payload, 0), ReadU32(payload, 4));
    }

    public static Net7GadgetControlUpdate DecodeGadgetControl(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 6)
            throw new InvalidDataException("0x0067 Gadget Control payload is shorter than NameSize + Function.");
        ushort nameSize = BinaryPrimitives.ReadUInt16LittleEndian(payload[..2]);
        int expected = checked(6 + nameSize);
        RequirePrefix(payload, expected, "0x0067 Gadget Control");
        byte[] nameBytes = payload.Slice(6, nameSize).ToArray();
        return new Net7GadgetControlUpdate(nameSize, ReadU32(payload, 2), nameBytes, DecodeCountedLatin1(nameBytes));
    }

    public static Net7ClickIndicatorUpdate DecodeClickIndicator(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, ClickIndicatorPayloadSize, "0x0069 Click Indicator");
        return new Net7ClickIndicatorUpdate(payload[0], ReadU32(payload, 1));
    }

    public static Net7StarbaseRoomUpdate DecodeStarbaseRoomUpdate(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, StarbaseRoomUpdatePayloadSize, "0x00A0 Starbase Room Update");
        return new Net7StarbaseRoomUpdate(ReadU32(payload, 0), ReadU32(payload, 4), ReadU32(payload, 8));
    }

    public static Net7ConfirmedActionOffer DecodeConfirmedActionOffer(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload, "0x00BE Confirmed Action Offer");
        uint type = reader.NetworkU32();
        uint id = BinaryPrimitives.ReverseEndianness(reader.InitializedU32());
        string message = reader.SharedString();
        return new Net7ConfirmedActionOffer(type, id, message);
    }

    public static Net7ConfirmedActionTimeout DecodeConfirmedActionTimeout(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload, "0x00BF Confirmed Action Timeout");
        uint type = reader.NetworkU32();
        RequireConfirmedActionType(type, "0x00BF Confirmed Action Timeout");
        // 008709E0 initializes ID to zero, then swaps the available bounded read.
        return new Net7ConfirmedActionTimeout(type, BinaryPrimitives.ReverseEndianness(reader.InitializedU32()));
    }

    private static void RequireConfirmedActionType(uint type, string context)
    {
        if (type > 1)
            throw new InvalidDataException($"{context} Type {type} is outside the current supported 0/1 domain.");
    }

    private static string DecodeCountedLatin1(ReadOnlySpan<byte> bytes)
    {
        int nul = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(nul < 0 ? bytes : bytes[..nul]);
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));

    private static void RequirePrefix(ReadOnlySpan<byte> payload, int expected, string context)
    {
        if (payload.Length < expected)
            throw new InvalidDataException($"{context} payload needs {expected} bytes; got {payload.Length}.");
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _payload;
        private readonly string _context;
        private int _offset;

        public Reader(ReadOnlySpan<byte> payload, string context)
        {
            _payload = payload;
            _context = context;
            _offset = 0;
        }

        public int Remaining => _payload.Length - _offset;
        public ReadOnlySpan<byte> RemainingSpan => _payload[_offset..];

        public byte U8()
        {
            Ensure(1, "u8");
            return _payload[_offset++];
        }

        public uint U32()
        {
            Ensure(4, "u32");
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_payload.Slice(_offset, 4));
            _offset += 4;
            return value;
        }

        /// <summary>004AACE0 bounded read over an existing value: the available
        /// bytes replace its low-order bytes and the rest are kept.</summary>
        public uint OverlayU32(uint retained)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, retained);
            int count = Math.Min(4, Remaining);
            RemainingSpan[..count].CopyTo(bytes);
            _offset += count;
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        public uint InitializedU32()
        {
            Span<byte> bytes = stackalloc byte[4];
            bytes.Clear();
            int count = Math.Min(4, Remaining);
            RemainingSpan[..count].CopyTo(bytes);
            _offset += count;
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        public uint NetworkU32()
        {
            Ensure(4, "network-u32");
            uint value = BinaryPrimitives.ReadUInt32BigEndian(_payload.Slice(_offset, 4));
            _offset += 4;
            return value;
        }

        public string CString()
        {
            int nul = RemainingSpan.IndexOf((byte)0);
            // 0086EED0 -> 004AAE90 keeps the available text at end of input.
            int length = nul < 0 ? Remaining : nul;
            string value = Encoding.Latin1.GetString(RemainingSpan[..length]);
            _offset += length + (nul < 0 ? 0 : 1);
            return value;
        }

        public string SharedString()
        {
            // 0086EB50 initializes the length to zero before its bounded read.
            Span<byte> bytes = stackalloc byte[2];
            bytes.Clear();
            int available = Math.Min(2, Remaining);
            RemainingSpan[..available].CopyTo(bytes);
            _offset += available;
            ushort count = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
            Ensure(count, "SharedString data");
            string value = DecodeCountedLatin1(_payload.Slice(_offset, count));
            _offset += count;
            return value;
        }

        public byte[] Bytes(uint count, string description)
        {
            int length = (int)count; // the caller bounds count by Remaining
            Ensure(length, description);
            byte[] value = _payload.Slice(_offset, length).ToArray();
            _offset += length;
            return value;
        }

        public int NetworkSignedCount()
        {
            // 008AF150/008AFD10 read the count into an uninitialized local and loop
            // while the signed value is positive: the four bytes are mandatory.
            int count = unchecked((int)NetworkU32());
            return count <= 0 ? 0 : count;
        }

        /// <summary>
        /// 008AF150/008AFD10 read each verb through a persistent stack local with
        /// 004AACE0's bounded copy. A short record overlays the available bytes on
        /// the retained value; once the payload is exhausted every remaining
        /// iteration re-inserts that same value into the native set, which is
        /// idempotent, so decoding stops there. A record before any retained value
        /// would read uninitialized storage (safety rule: undefined).
        /// </summary>
        public uint[] RetainedVerbRecords(int count, ref uint? retained, string description)
        {
            var values = new List<uint>(Math.Min(count, Remaining / 4 + 1));
            Span<byte> bytes = stackalloc byte[4];
            for (int i = 0; i < count; i++)
            {
                if (Remaining < 4 && retained is null)
                    throw new InvalidDataException($"{_context} {description} {i} reads uninitialized native storage: {Remaining} bytes remain.");
                if (Remaining == 0)
                {
                    // Only a list with no record of its own observes the retained
                    // value afresh; otherwise it is already this list's last entry.
                    if (values.Count == 0)
                        values.Add(retained!.Value);
                    break;
                }
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, retained ?? 0);
                int available = Math.Min(4, Remaining);
                RemainingSpan[..available].CopyTo(bytes);
                _offset += available;
                retained = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                values.Add(retained.Value);
            }
            return values.ToArray();
        }

        public void Advance(int count, string description)
        {
            Ensure(count, description);
            _offset += count;
        }

        private void Ensure(int count, string description)
        {
            if (count < 0 || count > Remaining)
                throw new InvalidDataException($"{_context} is truncated while reading {description}: need {count} bytes, {Remaining} remain.");
        }
    }
}
