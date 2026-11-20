using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Net7.Presentation;

public sealed record Net7JobListEntry(
    uint Field0,
    uint Field1,
    uint Field2,
    uint Field3,
    string Text0,
    string Text1,
    string Text2);

public sealed record Net7JobDescriptionUpdate(
    uint JobId,
    byte StillAvailable,
    string Title,
    string Description);

public sealed record Net7JobAcceptReplyUpdate(uint JobId, uint? AcceptStatus, uint SecondsRemaining);

public sealed record Net7NoteListEntry(
    uint NoteIndex,
    uint SentTimestamp,
    byte[] FromBytes,
    string From);

/// <summary>
/// Current retail 0x00B7 decoding has deliberately odd geometry: it admits a
/// row only while at least 76 bytes remain, but consumes 73 meaningful bytes
/// (two network-u32 values plus From[65]) per row. Any final &lt;76-byte tail is
/// ignored by retail. Retaining it here makes that compatibility behavior
/// explicit rather than silently inventing per-record padding.
/// </summary>
public sealed record Net7NoteListUpdate(
    IReadOnlyList<Net7NoteListEntry> Entries,
    byte[] IgnoredTrailingBytes);

public sealed record Net7NoteContentUpdate(
    uint SentTimestamp,
    byte[] FromBytes,
    string From,
    string Content);

public sealed record Net7ReferralPointsUpdate(uint PointsTotal, uint PointsSpent);
public sealed record Net7GmInventoryEditorUpdate(uint PlayerPackTargetValue, IReadOnlyList<string> Entries);
public sealed record Net7GmMissionLogUpdate(uint Field0, IReadOnlyList<string> Entries);

/// <summary>
/// Current-client codecs for jobs, notes, referral and GM/admin inbound
/// packet families. Raw u32 job fields remain little-endian; fields proven to
/// use the retail network-u32 helpers are converted from big-endian here.
/// </summary>
public static class Net7JobsAdminWire
{
    public const int JobDeletePayloadSize = 4;
    public const int JobAcceptReplyPayloadSize = 12;
    public const int NoteFromSize = 65;
    public const int NoteListAdmissionSize = 76;
    public const int NoteListMeaningfulRecordSize = 73;
    public const int ReferralPayloadSize = 8;

    public static IReadOnlyList<Net7JobListEntry> DecodeJobList(ReadOnlySpan<byte> payload)
    {
        // 00895F80 reads the signed count into uninitialized packet storage and
        // each record's four u32 fields into fresh AllocateMemory(0x40) storage;
        // only the CStrings (004AAE90) are defined at the end of the payload.
        // Safety rule: stop at the first undefined record read and keep the
        // complete records decoded before it.
        var reader = new Reader(payload, "0x0093 Job List");
        int count = unchecked((int)reader.U32());
        var entries = new List<Net7JobListEntry>();
        for (int i = 0; i < count && reader.Remaining >= 16; i++)
        {
            entries.Add(new Net7JobListEntry(
                reader.U32(), reader.U32(), reader.U32(), reader.U32(),
                reader.CString(), reader.CString(), reader.CString()));
        }
        return entries;
    }

    public static Net7JobDescriptionUpdate DecodeJobDescription(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload, "0x0094 Job Description");
        var update = new Net7JobDescriptionUpdate(
            reader.InitializedU32(), reader.InitializedU8(), reader.CString(), reader.CString());
        return update;
    }

    public static uint DecodeJobDelete(ReadOnlySpan<byte> payload)
    {
        // Retail 00896D10 initializes the ID before its bounded read.
        var reader = new Reader(payload, "0x0095 Job Delete");
        return reader.InitializedU32();
    }

    public static Net7JobAcceptReplyUpdate DecodeJobAcceptReply(ReadOnlySpan<byte> payload)
    {
        // Current Net7 emits an ID-only reply (2026-09-10 passive receipt).
        // Retail 00896F60/004AACE0 tolerates the absent trailing reads. Its
        // allocator does not initialize them: do not invent a success status.
        if (payload.Length == 4)
            return new Net7JobAcceptReplyUpdate(BinaryPrimitives.ReadUInt32LittleEndian(payload), null, 0);
        if (payload.Length < JobAcceptReplyPayloadSize)
            throw new InvalidDataException("0x0096 Job Accept Reply is truncated.");
        return new Net7JobAcceptReplyUpdate(
            BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4)));
    }

    public static Net7NoteListUpdate DecodeNoteList(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload, "0x00B7 Note List");
        var entries = new List<Net7NoteListEntry>();

        // Packet_00B7_NoteList__ctor_decode @ 0x0087EB30 compares the
        // remaining DynamicByteBuffer count against 0x4C (76), then consumes
        // only 4 + 4 + 65 = 73 bytes. This intentionally mirrors that current
        // decoder rather than the older "77-byte record" shorthand.
        while (reader.Remaining >= NoteListAdmissionSize)
        {
            uint noteIndex = reader.NetworkU32();
            uint sentTimestamp = reader.NetworkU32();
            byte[] fromBytes = reader.Bytes(NoteFromSize, "From[65]");
            entries.Add(new Net7NoteListEntry(
                noteIndex,
                sentTimestamp,
                fromBytes,
                DecodeFixedLatin1(fromBytes)));
        }

        byte[] ignoredTail = reader.Bytes(reader.Remaining, "retail-ignored Note List tail");
        return new Net7NoteListUpdate(entries, ignoredTail);
    }

    public static Net7NoteContentUpdate DecodeNoteContent(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload, "0x00B8 Note Content");
        uint sentTimestamp = reader.NetworkU32();
        byte[] fromBytes = reader.Bytes(NoteFromSize, "From[65]");
        string content = reader.CString();
        return new Net7NoteContentUpdate(sentTimestamp, fromBytes, DecodeFixedLatin1(fromBytes), content);
    }

    public static Net7ReferralPointsUpdate DecodeReferralPoints(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload, "0x00E9 Referral Points Available");
        return new Net7ReferralPointsUpdate(reader.InitializedNetworkU32(), reader.InitializedNetworkU32());
    }

    public static Net7GmInventoryEditorUpdate DecodeGmEditPlayerInventory(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload, "0x00EA GM Edit Player Inventory");
        uint target = reader.InitializedNetworkU32();
        int count = reader.InitializedSignedCount();
        var entries = new string[count];
        for (int i = 0; i < count; i++)
            entries[i] = reader.CString();
        return new Net7GmInventoryEditorUpdate(target, entries);
    }

    public static Net7GmMissionLogUpdate DecodeGmViewMissionLog(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload, "0x00EB GM View Mission Log");
        uint field0 = reader.InitializedNetworkU32();
        int count = reader.InitializedSignedCount();
        var entries = new string[count];
        for (int i = 0; i < count; i++)
            entries[i] = reader.CString();
        return new Net7GmMissionLogUpdate(field0, entries);
    }

    private static string DecodeFixedLatin1(ReadOnlySpan<byte> bytes)
    {
        int nul = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(nul < 0 ? bytes : bytes[..nul]);
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
        private ReadOnlySpan<byte> RemainingSpan => _payload[_offset..];

        public byte InitializedU8() => Remaining == 0 ? (byte)0 : _payload[_offset++];

        public uint InitializedU32()
        {
            Span<byte> value = stackalloc byte[4];
            value.Clear();
            int count = Math.Min(4, Remaining);
            RemainingSpan[..count].CopyTo(value);
            _offset += count;
            return BinaryPrimitives.ReadUInt32LittleEndian(value);
        }

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
            // Retail 004AAE90 copies the available prefix and appends NUL.
            int length = nul < 0 ? Remaining : nul;
            string value = Encoding.Latin1.GetString(RemainingSpan[..length]);
            _offset += length + (nul < 0 ? 0 : 1);
            return value;
        }

        public byte[] Bytes(int count, string description)
        {
            Ensure(count, description);
            byte[] value = _payload.Slice(_offset, count).ToArray();
            _offset += count;
            return value;
        }

        // Retail 0086F630/0086FD00 initializes both fields and uses a signed loop count.
        public uint InitializedNetworkU32() => BinaryPrimitives.ReverseEndianness(InitializedU32());
        public int InitializedSignedCount() => Math.Max(0, unchecked((int)InitializedNetworkU32()));

        private void Ensure(int count, string description)
        {
            if (count < 0 || count > Remaining)
                throw new InvalidDataException($"{_context} is truncated while reading {description}: need {count} bytes, {Remaining} remain.");
        }
    }
}
