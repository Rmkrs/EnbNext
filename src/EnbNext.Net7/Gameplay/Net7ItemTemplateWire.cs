using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Net7.Gameplay;

public enum Net7ItemTemplateAttributeType
{
    Int = 1,
    Float = 2,
    String = 4,
}

/// <summary>
/// Raw packet SharedString bytes plus the C-string-effective view used by current retail consumers.
/// Packet SharedStrings do not carry a wire NUL of their own; an embedded NUL is still consumed.
/// </summary>
public sealed record Net7ItemTemplateSharedString(byte[] WireBytes)
{
    public string EffectiveText
    {
        get
        {
            int nul = Array.IndexOf(WireBytes, (byte)0);
            int count = nul >= 0 ? nul : WireBytes.Length;
            return Encoding.Latin1.GetString(WireBytes, 0, count);
        }
    }
}

/// <summary>One ItemTemplateInfo attribute. RawValue holds Int/Float bits; StringWireBytes includes its wire NUL.</summary>
public sealed record Net7ItemTemplateAttribute(
    uint AttributeId,
    Net7ItemTemplateAttributeType Type,
    uint RawValue,
    byte[]? StringWireBytes)
{
    public int Int32Value => unchecked((int)RawValue);
    public float FloatValue => BitConverter.Int32BitsToSingle(unchecked((int)RawValue));
    public string StringValue
    {
        get
        {
            if (StringWireBytes is null)
                return string.Empty;
            int nul = Array.IndexOf(StringWireBytes, (byte)0);
            int count = nul >= 0 ? nul : StringWireBytes.Length;
            return Encoding.Latin1.GetString(StringWireBytes, 0, count);
        }
    }
}

public sealed record Net7ItemTemplateVectorEntry(
    Net7ItemTemplateSharedString StringA,
    Net7ItemTemplateSharedString StringB,
    Net7ItemTemplateSharedString StringC,
    IReadOnlyList<uint> ListA,
    IReadOnlyList<uint> ListB,
    uint Field50,
    uint Field54);

/// <summary>Four wire values read whenever the wire vector count is nonzero, including negative counts.</summary>
public sealed record Net7ItemTemplateVectorMetadata(
    uint ClampedTimeOrDuration,
    uint Field18RawBits,
    uint Field1C,
    uint Field20)
{
    public float Field18Float => BitConverter.Int32BitsToSingle(unchecked((int)Field18RawBits));
}

public sealed record Net7ItemTemplateVector(
    IReadOnlyList<Net7ItemTemplateVectorEntry> Entries,
    Net7ItemTemplateVectorMetadata? Metadata);

public sealed record Net7ItemTemplateInfo(
    IReadOnlyList<Net7ItemTemplateAttribute> Attributes,
    Net7ItemTemplateVector VectorA,
    Net7ItemTemplateVector VectorB);

public sealed record Net7ItemTemplate(
    uint ItemTemplateId,
    uint Category,
    uint Subcategory,
    uint ItemType,
    Net7ItemTemplateInfo Info,
    ushort GameBasset,
    ushort IconBaseAsset,
    ushort TechLevel,
    uint Cost,
    uint MaxStack,
    uint EffectUsage,
    uint Flags,
    Net7ItemTemplateSharedString Name,
    Net7ItemTemplateSharedString StringFieldB,
    Net7ItemTemplateSharedString StringFieldC);

/// <summary>Exact retail 0x0025 payload codec.</summary>
public static class Net7ItemTemplateWire
{
    public static Net7ItemTemplate Decode(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload);
        uint id = reader.U32Be("ItemTemplateID");
        uint category = reader.U32Le("Category");
        uint subcategory = reader.U32Le("Subcategory");
        uint itemType = reader.U32Le("ItemType");
        Net7ItemTemplateInfo info = DecodeInfo(ref reader);
        ushort gameBasset = reader.U16Be("GameBasset");
        ushort iconBaseAsset = reader.U16Be("IconBaseAsset");
        ushort techLevel = reader.U16Be("TechLevel");
        uint cost = reader.U32Be("Cost");
        uint maxStack = reader.U32Be("MaxStack");
        uint effectUsage = reader.U32Be("EffectUsage");
        uint flags = reader.U32Le("Flags");
        // 008955C0 reads the three trailing SharedStrings through 0086EB50,
        // whose length starts at zero: a payload ending here leaves them empty.
        // Safety rule: a body running past the payload keeps the available bytes.
        Net7ItemTemplateSharedString name = reader.TrailingSharedString();
        Net7ItemTemplateSharedString fieldB = reader.TrailingSharedString();
        Net7ItemTemplateSharedString fieldC = reader.TrailingSharedString();
        // 008955C0 returns after the third SharedString; unused bytes are ignored.
        return new Net7ItemTemplate(
            id, category, subcategory, itemType, info,
            gameBasset, iconBaseAsset, techLevel,
            cost, maxStack, effectUsage, flags, name, fieldB, fieldC);
    }

    public static byte[] Encode(Net7ItemTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        using var stream = new MemoryStream();
        var writer = new Writer(stream);
        writer.U32Be(template.ItemTemplateId);
        writer.U32Le(template.Category);
        writer.U32Le(template.Subcategory);
        writer.U32Le(template.ItemType);
        EncodeInfo(writer, template.Info);
        writer.U16Be(template.GameBasset);
        writer.U16Be(template.IconBaseAsset);
        writer.U16Be(template.TechLevel);
        writer.U32Be(template.Cost);
        writer.U32Be(template.MaxStack);
        writer.U32Be(template.EffectUsage);
        writer.U32Le(template.Flags);
        writer.SharedString(template.Name, "Name");
        writer.SharedString(template.StringFieldB, "string field B");
        writer.SharedString(template.StringFieldC, "string field C");
        return stream.ToArray();
    }

    public static Net7ItemTemplateAttributeType GetAttributeType(uint attributeId)
        => attributeId switch
        {
            // 0083F320 (startup table BF34E0, read by 00840030) also types 30 as integer and 0, 36, 37 as float (INV-D19).
            2 or 3 or 4 or 5 or 6 or 7 or 8 or 12 or 14 or 15 or 16 or 17 or 18 or 19 or 22 or 24 or 28 or 29 or 30 or 31 or 32 or 33 or 35
                => Net7ItemTemplateAttributeType.Int,
            0 or 9 or 10 or 20 or 21 or 23 or 25 or 26 or 34 or 36 or 37
                => Net7ItemTemplateAttributeType.Float,
            1 or 11 or 13 or 27
                => Net7ItemTemplateAttributeType.String,
            _ => throw new InvalidDataException($"ItemTemplateInfo attribute ID {attributeId} has no proven current-client wire type."),
        };

    private static Net7ItemTemplateInfo DecodeInfo(ref Reader reader)
    {
        int count = reader.U8("ItemTemplateInfo attribute count");
        var attributes = new Net7ItemTemplateAttribute[count];
        for (int i = 0; i < count; i++)
        {
            uint id = reader.U32Le($"ItemTemplateInfo attribute {i} ID");
            Net7ItemTemplateAttributeType type = GetAttributeType(id);
            attributes[i] = type switch
            {
                Net7ItemTemplateAttributeType.Int => new Net7ItemTemplateAttribute(id, type, reader.U32Le($"attribute {id} int"), null),
                Net7ItemTemplateAttributeType.Float => new Net7ItemTemplateAttribute(id, type, reader.U32Le($"attribute {id} float bits"), null),
                Net7ItemTemplateAttributeType.String => new Net7ItemTemplateAttribute(id, type, 0, reader.ItemInfoString($"attribute {id} string")),
                _ => throw new InvalidDataException($"Unsupported ItemTemplateInfo attribute type {type}."),
            };
        }

        return new Net7ItemTemplateInfo(attributes, DecodeVector(ref reader, "vector A"), DecodeVector(ref reader, "vector B"));
    }

    private static Net7ItemTemplateVector DecodeVector(ref Reader reader, string label)
    {
        uint countRaw = reader.U32Be($"ItemTemplateInfo {label} count");
        // 0083E040 tests nonzero for metadata, but iterates entries with JLE.
        int count = Math.Max(0, unchecked((int)countRaw));
        long minimumBytes = (long)count * 22 + (countRaw == 0 ? 0 : 16);
        if (minimumBytes > reader.Remaining)
            throw new InvalidDataException($"ItemTemplateInfo {label} count {count} cannot fit in the remaining {reader.Remaining} payload byte(s).");
        var entries = new Net7ItemTemplateVectorEntry[count];
        for (int i = 0; i < count; i++)
        {
            Net7ItemTemplateSharedString a = reader.SharedString($"{label}[{i}] string A");
            Net7ItemTemplateSharedString b = reader.SharedString($"{label}[{i}] string B");
            Net7ItemTemplateSharedString c = reader.SharedString($"{label}[{i}] string C");
            IReadOnlyList<uint> listA = reader.U32BeList($"{label}[{i}] list A");
            IReadOnlyList<uint> listB = reader.U32BeList($"{label}[{i}] list B");
            uint field50 = reader.U32Le($"{label}[{i}] field50");
            uint field54 = reader.U32Le($"{label}[{i}] field54");
            entries[i] = new Net7ItemTemplateVectorEntry(a, b, c, listA, listB, field50, field54);
        }

        Net7ItemTemplateVectorMetadata? metadata = null;
        if (countRaw != 0)
        {
            metadata = new Net7ItemTemplateVectorMetadata(
                reader.U32Be($"{label} metadata time/duration"),
                reader.U32Be($"{label} metadata field18 bits"),
                reader.U32Be($"{label} metadata field1C"),
                reader.U32Be($"{label} metadata field20"));
        }
        return new Net7ItemTemplateVector(entries, metadata);
    }

    private static void EncodeInfo(Writer writer, Net7ItemTemplateInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.Attributes.Count > byte.MaxValue)
            throw new InvalidDataException("ItemTemplateInfo attribute count exceeds u8 wire range.");
        writer.U8(checked((byte)info.Attributes.Count));
        foreach (Net7ItemTemplateAttribute attribute in info.Attributes)
        {
            Net7ItemTemplateAttributeType expected = GetAttributeType(attribute.AttributeId);
            if (attribute.Type != expected)
                throw new InvalidDataException($"ItemTemplateInfo attribute {attribute.AttributeId} is {attribute.Type}; current type table requires {expected}.");
            writer.U32Le(attribute.AttributeId);
            switch (attribute.Type)
            {
                case Net7ItemTemplateAttributeType.Int:
                case Net7ItemTemplateAttributeType.Float:
                    writer.U32Le(attribute.RawValue);
                    break;
                case Net7ItemTemplateAttributeType.String:
                    writer.ItemInfoString(attribute.StringWireBytes, $"attribute {attribute.AttributeId} string");
                    break;
                default:
                    throw new InvalidDataException($"Unsupported ItemTemplateInfo attribute type {attribute.Type}.");
            }
        }
        EncodeVector(writer, info.VectorA, "vector A");
        EncodeVector(writer, info.VectorB, "vector B");
    }

    private static void EncodeVector(Writer writer, Net7ItemTemplateVector vector, string label)
    {
        ArgumentNullException.ThrowIfNull(vector);
        writer.U32Be(checked((uint)vector.Entries.Count));
        for (int i = 0; i < vector.Entries.Count; i++)
        {
            Net7ItemTemplateVectorEntry entry = vector.Entries[i];
            writer.SharedString(entry.StringA, $"{label}[{i}] string A");
            writer.SharedString(entry.StringB, $"{label}[{i}] string B");
            writer.SharedString(entry.StringC, $"{label}[{i}] string C");
            writer.U32BeList(entry.ListA);
            writer.U32BeList(entry.ListB);
            writer.U32Le(entry.Field50);
            writer.U32Le(entry.Field54);
        }

        if (vector.Entries.Count == 0)
        {
            if (vector.Metadata is not null)
                throw new InvalidDataException($"ItemTemplateInfo {label} cannot serialize metadata when its element count is zero.");
            return;
        }

        Net7ItemTemplateVectorMetadata metadata = vector.Metadata
            ?? throw new InvalidDataException($"ItemTemplateInfo {label} requires four metadata dwords when non-empty.");
        writer.U32Be(metadata.ClampedTimeOrDuration);
        writer.U32Be(metadata.Field18RawBits);
        writer.U32Be(metadata.Field1C);
        writer.U32Be(metadata.Field20);
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _payload;
        private int _offset;

        public Reader(ReadOnlySpan<byte> payload) => _payload = payload;

        public int Remaining => _payload.Length - _offset;

        public byte U8(string label)
        {
            Ensure(1, label);
            return _payload[_offset++];
        }

        public ushort U16Le(string label)
        {
            Ensure(2, label);
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_payload[_offset..]);
            _offset += 2;
            return value;
        }

        public ushort U16Be(string label)
        {
            Ensure(2, label);
            ushort value = BinaryPrimitives.ReadUInt16BigEndian(_payload[_offset..]);
            _offset += 2;
            return value;
        }

        public uint U32Le(string label)
        {
            Ensure(4, label);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_payload[_offset..]);
            _offset += 4;
            return value;
        }

        public uint U32Be(string label)
        {
            Ensure(4, label);
            uint value = BinaryPrimitives.ReadUInt32BigEndian(_payload[_offset..]);
            _offset += 4;
            return value;
        }

        public Net7ItemTemplateSharedString SharedString(string label)
        {
            ushort length = U16Le(label + " length");
            return new Net7ItemTemplateSharedString(Bytes(length, label));
        }

        public Net7ItemTemplateSharedString TrailingSharedString()
        {
            Span<byte> length = stackalloc byte[2];
            length.Clear();
            int available = Math.Min(2, Remaining);
            _payload.Slice(_offset, available).CopyTo(length);
            _offset += available;
            int count = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(length), Remaining);
            byte[] value = _payload.Slice(_offset, count).ToArray();
            _offset += count;
            return new Net7ItemTemplateSharedString(value);
        }

        public byte[] ItemInfoString(string label)
        {
            ushort length = U16Le(label + " length");
            if (length == 0)
                throw new InvalidDataException($"{label} is missing the NUL included by the current ItemTemplateInfo string grammar.");
            byte[] bytes = Bytes(length, label);
            // 00840030 retains the declared bytes; 00841100 displays through
            // the first NUL, which need not be the final byte in that range.
            if (Array.IndexOf(bytes, (byte)0) < 0)
                throw new InvalidDataException($"{label} has no bounded NUL terminator.");
            return bytes;
        }

        public IReadOnlyList<uint> U32BeList(string label)
        {
            uint countRaw = U32Be(label + " count");
            // 00832CA0 iterates each list only while the signed count is positive.
            int count = Math.Max(0, unchecked((int)countRaw));
            if ((long)count * 4 > Remaining)
                throw new InvalidDataException($"{label} count {count} cannot fit in the remaining {Remaining} payload byte(s).");
            var values = new uint[count];
            for (int i = 0; i < count; i++)
                values[i] = U32Be($"{label}[{i}]");
            return values;
        }

        private byte[] Bytes(int count, string label)
        {
            Ensure(count, label);
            byte[] value = _payload.Slice(_offset, count).ToArray();
            _offset += count;
            return value;
        }

        private void Ensure(int count, string label)
        {
            if (count < 0 || _offset > _payload.Length - count)
                throw new InvalidDataException($"retail 0x0025 payload is truncated while reading {label} at byte {_offset}; need {count} byte(s), have {_payload.Length - _offset}.");
        }
    }

    private sealed class Writer
    {
        private readonly Stream _stream;
        private readonly byte[] _scratch = new byte[4];

        public Writer(Stream stream) => _stream = stream;

        public void U8(byte value) => _stream.WriteByte(value);
        public void U16Le(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(_scratch, value); _stream.Write(_scratch, 0, 2); }
        public void U16Be(ushort value) { BinaryPrimitives.WriteUInt16BigEndian(_scratch, value); _stream.Write(_scratch, 0, 2); }
        public void U32Le(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(_scratch, value); _stream.Write(_scratch, 0, 4); }
        public void U32Be(uint value) { BinaryPrimitives.WriteUInt32BigEndian(_scratch, value); _stream.Write(_scratch, 0, 4); }

        public void SharedString(Net7ItemTemplateSharedString value, string label)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.WireBytes.Length > ushort.MaxValue)
                throw new InvalidDataException($"{label} exceeds the u16 packet SharedString wire length.");
            U16Le(checked((ushort)value.WireBytes.Length));
            _stream.Write(value.WireBytes);
        }

        public void ItemInfoString(byte[]? bytes, string label)
        {
            if (bytes is null || bytes.Length == 0 || bytes[^1] != 0)
                throw new InvalidDataException($"{label} must include its terminating NUL on wire.");
            if (bytes.Length > ushort.MaxValue)
                throw new InvalidDataException($"{label} exceeds the u16 ItemTemplateInfo string wire length.");
            U16Le(checked((ushort)bytes.Length));
            _stream.Write(bytes);
        }

        public void U32BeList(IReadOnlyList<uint> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            U32Be(checked((uint)values.Count));
            foreach (uint value in values)
                U32Be(value);
        }
    }
}
