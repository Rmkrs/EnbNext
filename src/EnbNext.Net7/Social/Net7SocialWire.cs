using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Net7.Social;

/// <summary>
/// Current retail social/chat inbound codecs.
/// Historical compile-excluded packet records are not used by this runtime layer.
/// </summary>
public static class Net7SocialWire
{
    private static readonly Encoding WireText = Encoding.Latin1;

    public static Net7GroupActionUpdate DecodeGroupAction(ReadOnlySpan<byte> payload)
    {
        Require(payload, 3, "0x001E Group Action");
        ushort size = BinaryPrimitives.ReadUInt16LittleEndian(payload[..2]);
        byte type = payload[2];
        // 00888890 copies the available prefix. 00736930 consumes only the
        // type-1 C string; other types do not read these bytes at all.
        byte[] data = payload.Slice(3, Math.Min(size, payload.Length - 3)).ToArray();
        string? invite = null;
        if (type == 1)
        {
            // Safety rule: without a NUL the C string runs past the copied bytes
            // into undefined memory; only the available bytes are decoded.
            int end = data.AsSpan().IndexOf((byte)0);
            invite = WireText.GetString(end < 0 ? data : data.AsSpan(0, end));
        }
        return new Net7GroupActionUpdate(type, data) { InviteText = invite };
    }

    public static IReadOnlyList<Net7FindMemberRecord> DecodeFindMember(ReadOnlySpan<byte> payload)
    {
        Require(payload, 4, "0x0053 FindMember");
        // 00886710 iterates only a positive signed count; trailing bytes are unused.
        int count = BinaryPrimitives.ReadInt32LittleEndian(payload[..4]);
        if (count <= 0) return Array.Empty<Net7FindMemberRecord>();
        // Safety rule: each record is a fresh uninitialized 16-byte allocation, so
        // a record past the end of the packet is undefined; complete records are kept.
        var rows = new List<Net7FindMemberRecord>(Math.Min(count, (payload.Length - 4) / 16));
        int offset = 4;
        for (int i = 0; i < count && offset + 16 <= payload.Length; i++)
        {
            uint objectId = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4)); offset += 4;
            uint numeric = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4)); offset += 4;
            uint race = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4)); offset += 4;
            uint profession = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4)); offset += 4;
            rows.Add(new Net7FindMemberRecord(objectId, numeric, race, profession));
        }
        return rows;
    }

    public static Net7MessageChatstreamUpdate DecodeMessageChatstream(ReadOnlySpan<byte> payload)
    {
        Require(payload, 7, "0x005F MessageChatstream");
        ushort chatSize = BinaryPrimitives.ReadUInt16LittleEndian(payload[..2]);
        if (chatSize > short.MaxValue)
            throw new InvalidDataException($"0x005F ChatSize {chatSize} exceeds the current signed-short-safe range.");
        if (payload.Length < 7 + chatSize)
            throw new InvalidDataException($"0x005F ChatSize {chatSize} does not match payload length {payload.Length}.");
        byte type = payload[2];
        uint shipId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(3, 4));
        IReadOnlyList<Net7PlayerNlpSegment> segments = DecodePlayerNlp(payload.Slice(7, chatSize));
        return new Net7MessageChatstreamUpdate(chatSize, type, shipId, segments);
    }

    public static Net7ClientChatListUpdate DecodeClientChatList(ReadOnlySpan<byte> payload)
    {
        // 00879850 initializes ListKind to 0xFFFFFFFF and each count to zero
        // before bounded reads; a short packet is a defined empty list.
        int offset = 0;
        uint kind = ReadInitializedU32(payload, ref offset, uint.MaxValue);
        string context = ReadSharedString(payload, ref offset, "0x00A4 Context");
        IReadOnlyList<string> a = ReadSharedStringList(payload, ref offset, "0x00A4 ArrayA");
        IReadOnlyList<string> b = ReadSharedStringList(payload, ref offset, "0x00A4 ArrayB");
        return new Net7ClientChatListUpdate(kind, context, a, b);
    }

    public static Net7ClientChatEventUpdate DecodeClientChatEvent(ReadOnlySpan<byte> payload)
    {
        int offset = 0;
        uint eventType = ReadInitializedU32(payload, ref offset, 0);
        uint eventField = ReadInitializedU32(payload, ref offset, 0);
        string s1 = ReadSharedString(payload, ref offset, "0x00A5 SS1");
        string s2 = ReadSharedString(payload, ref offset, "0x00A5 SS2");
        string s3 = ReadSharedString(payload, ref offset, "0x00A5 SS3");
        string s4 = ReadSharedString(payload, ref offset, "0x00A5 SS4");
        string s5 = ReadSharedString(payload, ref offset, "0x00A5 SS5");
        string s6 = ReadSharedString(payload, ref offset, "0x00A5 SS6");
        uint blobLength = ReadInitializedU32(payload, ref offset, 0);
        if (blobLength > int.MaxValue || offset + (long)blobLength > payload.Length)
            throw new InvalidDataException("0x00A5 BlobLength does not match the supplied payload.");
        byte[] blob = payload.Slice(offset, checked((int)blobLength)).ToArray();
        return new Net7ClientChatEventUpdate(eventType, eventField, s1, s2, s3, s4, s5, s6, blob);
    }

    public static Net7ClientChatErrorUpdate DecodeClientChatError(ReadOnlySpan<byte> payload)
    {
        int offset = 0;
        uint status = ReadInitializedU32(payload, ref offset, 1);
        uint request = ReadInitializedU32(payload, ref offset, uint.MaxValue);
        string s1 = ReadSharedString(payload, ref offset, "0x00A6 SS1");
        string s2 = ReadSharedString(payload, ref offset, "0x00A6 SS2");
        string s3 = ReadSharedString(payload, ref offset, "0x00A6 SS3");
        return new Net7ClientChatErrorUpdate(status, request, s1, s2, s3);
    }

    public static Net7CtaResponseUpdate DecodeCtaResponse(ReadOnlySpan<byte> payload)
    {
        // 0086F3C0 initializes both IDs to -1 and status to zero before the
        // bounded reads. 0073DAA0 dispatches RequestType without using status.
        Span<byte> fields = stackalloc byte[9];
        fields[..8].Fill(0xff);
        fields[8] = 0;
        payload[..Math.Min(payload.Length, fields.Length)].CopyTo(fields);
        return new Net7CtaResponseUpdate(
            BinaryPrimitives.ReadUInt32LittleEndian(fields[..4]),
            BinaryPrimitives.ReadUInt32LittleEndian(fields.Slice(4, 4)),
            fields[8]);
    }

    public static IReadOnlyList<Net7PlayerNlpSegment> DecodePlayerNlp(ReadOnlySpan<byte> stream)
    {
        // 004C48C0 / 00784700 walk the records in memory without a bound. Safety
        // rule: a header or payload past the end of the stream is undefined, so
        // decoding stops there and the records decoded so far are kept.
        var rows = new List<Net7PlayerNlpSegment>();
        int offset = 0;
        while (true)
        {
            if (offset + 4 > stream.Length)
                break;
            byte type = stream[offset];
            byte arg = stream[offset + 1];
            ushort storedLength = BinaryPrimitives.ReadUInt16LittleEndian(stream.Slice(offset + 2, 2));
            offset += 4;
            if (type == 8)
            {
                // 00784700 / 004C48C0 stop on type alone, without consuming its payload.
                break;
            }
            byte[] bytes = stream.Slice(offset, Math.Min((int)storedLength, stream.Length - offset)).ToArray();
            string? text = null;
            if (type == 1)
            {
                // 004C48C0 appends the C string at the payload, which may run on
                // into later records; it stops at the stream end (safety rule).
                ReadOnlySpan<byte> rest = stream[offset..];
                int terminator = rest.IndexOf((byte)0);
                text = WireText.GetString(terminator < 0 ? rest : rest[..terminator]);
            }
            rows.Add(new Net7PlayerNlpSegment(type, arg, bytes, text));
            // The length is a signed short in retail; a negative one walks back
            // over earlier bytes (no defined end), so it also stops decoding.
            if ((short)storedLength < 0 || offset + storedLength > stream.Length)
                break;
            offset += storedLength;
        }
        return rows;
    }

    private static IReadOnlyList<string> ReadSharedStringList(ReadOnlySpan<byte> payload, ref int offset, string label)
    {
        uint count = ReadInitializedU32(payload, ref offset, 0);
        // Safety rule: retail keeps reading declared entries past the end of the
        // packet (0086EB50/004AACE0 leave each length at zero). Next stops once
        // the payload is exhausted and keeps the entries decoded so far.
        var result = new List<string>((int)Math.Min(count, (uint)((payload.Length - offset) / 2)));
        for (uint i = 0; i < count && offset < payload.Length; i++)
            result.Add(ReadSharedString(payload, ref offset, label));
        return result;
    }

    private static string ReadSharedString(ReadOnlySpan<byte> payload, ref int offset, string label)
    {
        // 0086EB50 starts the length at zero; 004AACE0 copies only available bytes.
        Span<byte> length = stackalloc byte[2];
        length.Clear();
        int count = Math.Min(2, payload.Length - offset);
        payload.Slice(offset, count).CopyTo(length);
        offset += count;
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(length);
        // 0086EB50 consumes the declared bytes but assigns only through the first
        // NUL. Safety rule: past the end of the packet its buffer is undefined, so
        // only the available bytes are decoded and reading stops at the end.
        ReadOnlySpan<byte> bytes = payload.Slice(offset, Math.Min((int)len, payload.Length - offset));
        int terminator = bytes.IndexOf((byte)0);
        string value = WireText.GetString(terminator < 0 ? bytes : bytes[..terminator]);
        offset += bytes.Length;
        return value;
    }

    private static uint ReadInitializedU32(ReadOnlySpan<byte> payload, ref int offset, uint initial)
    {
        // 008790B0 / 0087A560 initialize fields before bounded packet reads.
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, initial);
        int count = Math.Min(4, payload.Length - offset);
        payload.Slice(offset, count).CopyTo(bytes);
        offset += count;
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static void Require(ReadOnlySpan<byte> payload, int minimum, string label)
    {
        if (payload.Length < minimum)
            throw new InvalidDataException($"{label} payload is shorter than {minimum} bytes.");
    }
}
