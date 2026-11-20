using System.Buffers.Binary;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Tests;

public sealed class Net7CDataCatalogTests
{
    [Fact]
    public void SyntheticFileDecodesIndependentHeadersIndexEntriesAndRecords()
    {
        byte[] file = BuildFile((5u, new byte[] { 1, 2, 3 }), (9u, new byte[] { 4, 5 }));
        Net7CDataCatalog catalog = Net7CDataCatalog.Decode(file);

        Assert.Equal(2, catalog.RecordCount);
        Assert.True(catalog.TryGetRecord(5, out Net7CDataRecord? first));
        Net7CDataRecord firstValue = Assert.IsType<Net7CDataRecord>(first);
        Assert.Equal(new byte[] { 1, 2, 3 }, firstValue.Payload);
        Assert.True(catalog.TryGetRecord(9, out Net7CDataRecord? second));
        Net7CDataRecord secondValue = Assert.IsType<Net7CDataRecord>(second);
        Assert.Equal(new byte[] { 4, 5 }, secondValue.Payload);
        Assert.False(catalog.TryGetRecord(7, out Net7CDataRecord? missing));
        Assert.Null(missing);
    }

    [Fact]
    public void RecordIdsNeedNotBeSortedWhenIndexOffsetsAreValid()
    {
        byte[] file = BuildFile((9u, new byte[] { 1, 2, 3 }), (5u, new byte[] { 4, 5 }));
        Net7CDataCatalog catalog = Net7CDataCatalog.Decode(file);

        Assert.True(catalog.TryGetRecord(9, out Net7CDataRecord? first));
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<Net7CDataRecord>(first).Payload);
        Assert.True(catalog.TryGetRecord(5, out Net7CDataRecord? second));
        Assert.Equal(new byte[] { 4, 5 }, Assert.IsType<Net7CDataRecord>(second).Payload);
    }

    [Fact]
    public void CDataRequestConsumesLittleEndianRecordIdPrefixAndIgnoresTrailingBytes()
    {
        Assert.Equal(0x78563412u, Net7GameplayWire.DecodeCDataRequest(Convert.FromHexString("12345678")));
        Assert.Equal(0x78563412u, Net7GameplayWire.DecodeCDataRequest(Convert.FromHexString("12345678AABBCC")));
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeCDataRequest(Convert.FromHexString("123456")));
    }

    [Fact]
    public void TruncatedIndexCannotBeRead()
    {
        byte[] valid = BuildFile((5u, new byte[] { 1 }), (9u, new byte[] { 2 }));
        Assert.Throws<InvalidDataException>(() => Net7CDataCatalog.Decode(valid.AsSpan(0, valid.Length - 1)));

    }

    [Fact]
    public void RepeatedRecordIdUsesLastEntryWithoutLosingOtherRecords()
    {
        byte[] file = BuildFile((5u, new byte[] { 1, 2 }), (9u, new byte[] { 3 }), (5u, new byte[] { 4, 5, 6 }));
        Net7CDataCatalog catalog = Net7CDataCatalog.Decode(file);
        Assert.Equal(2, catalog.RecordCount);
        Assert.True(catalog.TryGetRecord(5, out var repeated));
        Assert.Equal(new byte[] { 4, 5, 6 }, Assert.IsType<Net7CDataRecord>(repeated).Payload);
        Assert.True(catalog.TryGetRecord(9, out var other));
        Assert.Equal(new byte[] { 3 }, Assert.IsType<Net7CDataRecord>(other).Payload);
    }

    [Fact]
    public void ZeroIdDoesNotAdvancePreviousExtentOffset()
    {
        byte[] file = BuildFile((0u, new byte[] { 1 }), (5u, new byte[] { 2 }), (9u, new byte[] { 3 }));
        Net7CDataCatalog catalog = Net7CDataCatalog.Decode(file);
        Assert.False(catalog.TryGetRecord(0, out _));
        Assert.True(catalog.TryGetRecord(5, out var middle));
        byte[] expected = new byte[2];
        Net7CDataCatalog.DecodeBlock(file.AsSpan(5, 2), expected);
        Assert.Equal(expected, Assert.IsType<Net7CDataRecord>(middle).Payload);
        Assert.True(catalog.TryGetRecord(9, out var last));
        Assert.Equal(new byte[] { 3 }, Assert.IsType<Net7CDataRecord>(last).Payload);
    }

    [Fact]
    public void LastZeroIdReceivesFinalExtent()
    {
        byte[] file = BuildFile((5u, new byte[] { 1 }), (0u, new byte[] { 2 }));
        Net7CDataCatalog catalog = Net7CDataCatalog.Decode(file);
        Assert.True(catalog.TryGetRecord(5, out var first));
        Assert.Equal(new byte[] { 1 }, Assert.IsType<Net7CDataRecord>(first).Payload);
        Assert.True(catalog.TryGetRecord(0, out var last));
        Assert.Equal(new byte[] { 2 }, Assert.IsType<Net7CDataRecord>(last).Payload);
    }

    [Fact]
    public void UnusedFileTailDoesNotRejectIndexedRecords()
    {
        byte[] file = [.. BuildFile((5u, new byte[] { 1, 2, 3 })), 0xFF, 0x00, 0xAA];
        Net7CDataCatalog catalog = Net7CDataCatalog.Decode(file);
        Assert.True(catalog.TryGetRecord(5, out var record));
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<Net7CDataRecord>(record).Payload);
    }

    [Fact]
    public void NonPositiveRecordLengthsDoNotRejectOtherRecords()
    {
        byte[] unsortedOffsets = BuildFile(
            (5u, new byte[] { 1 }),
            (9u, new byte[] { 2 }),
            (12u, new byte[] { 3 }));
        int threeRecordIndex = GetIndexOffset(unsortedOffsets);
        RewriteIndexEntry(unsortedOffsets, threeRecordIndex, 1, 9u, 2u);
        RewriteIndexEntry(unsortedOffsets, threeRecordIndex, 2, 12u, 1u);
        Net7CDataCatalog descending = Net7CDataCatalog.Decode(unsortedOffsets);
        Assert.False(descending.TryGetRecord(9, out var missingDescending));
        Assert.Null(missingDescending);
        Assert.True(descending.TryGetRecord(12, out var retainedDescending));
        byte[] expected = new byte[2];
        Net7CDataCatalog.DecodeBlock(unsortedOffsets.AsSpan(5, 2), expected);
        Assert.Equal(expected, Assert.IsType<Net7CDataRecord>(retainedDescending).Payload);

        byte[] duplicateOffsets = BuildFile(
            (5u, new byte[] { 1 }),
            (9u, new byte[] { 2 }),
            (12u, new byte[] { 3 }));
        threeRecordIndex = GetIndexOffset(duplicateOffsets);
        RewriteIndexEntry(duplicateOffsets, threeRecordIndex, 2, 12u, 1u);
        Net7CDataCatalog equal = Net7CDataCatalog.Decode(duplicateOffsets);
        Assert.False(equal.TryGetRecord(9, out _));
        Assert.True(equal.TryGetRecord(5, out var retainedFirst));
        Assert.Equal(new byte[] { 1 }, Assert.IsType<Net7CDataRecord>(retainedFirst).Payload);

        byte[] skippedPrefix = BuildFile((5u, new byte[] { 1 }), (9u, new byte[] { 2 }));
        int twoRecordIndex = GetIndexOffset(skippedPrefix);
        RewriteIndexEntry(skippedPrefix, twoRecordIndex, 0, 5u, 1u);
        Net7CDataCatalog skipped = Net7CDataCatalog.Decode(skippedPrefix);
        Assert.True(skipped.TryGetRecord(5, out var shiftedFirst));
        Assert.Equal(new byte[] { 2 }, Assert.IsType<Net7CDataRecord>(shiftedFirst).Payload);
        Assert.True(skipped.TryGetRecord(9, out var unchangedLast));
        Assert.Equal(new byte[] { 2 }, Assert.IsType<Net7CDataRecord>(unchangedLast).Payload);

        byte[] zeroLengthTail = BuildFile((5u, new byte[] { 1 }), (9u, new byte[] { 2 }));
        twoRecordIndex = GetIndexOffset(zeroLengthTail);
        RewriteIndexEntry(zeroLengthTail, twoRecordIndex, 1, 9u, 2u);
        Net7CDataCatalog emptyTail = Net7CDataCatalog.Decode(zeroLengthTail);
        Assert.False(emptyTail.TryGetRecord(9, out _));
        Assert.True(emptyTail.TryGetRecord(5, out _));
    }

    [Fact]
    public void SingleRecordMayStartAfterUnusedPrefix()
    {
        byte[] file = BuildFile((5u, new byte[] { 1, 2, 3 }));
        RewriteIndexEntry(file, GetIndexOffset(file), 0, 5u, 1u);
        Net7CDataCatalog catalog = Net7CDataCatalog.Decode(file);
        Assert.True(catalog.TryGetRecord(5, out var record));
        byte[] expected = new byte[2];
        Net7CDataCatalog.DecodeBlock(file.AsSpan(5, 2), expected);
        Assert.Equal(expected, Assert.IsType<Net7CDataRecord>(record).Payload);
    }

    [Fact]
    public void BlockCodecMatchesReversePlusIndexPlus6AContract()
    {
        byte[] encoded = { 0x10, 0x20, 0x30, 0x40 };
        byte[] decoded = new byte[4];
        Net7CDataCatalog.DecodeBlock(encoded, decoded);
        Assert.Equal(new byte[] { 0xAA, 0x9B, 0x8C, 0x7D }, decoded);
    }

    internal static Net7CDataCatalog BuildCatalogForSession()
        => Net7CDataCatalog.Decode(BuildFile((0x11223344u, BuildSessionItemTemplatePayload())));

    internal static byte[] BuildSessionItemTemplatePayload()
    {
        var emptyVector = new Net7ItemTemplateVector(Array.Empty<Net7ItemTemplateVectorEntry>(), null);
        var template = new Net7ItemTemplate(
            0x11223344u,
            Category: 1,
            Subcategory: 2,
            ItemType: 3,
            new Net7ItemTemplateInfo(Array.Empty<Net7ItemTemplateAttribute>(), emptyVector, emptyVector),
            GameBasset: 0x0102,
            IconBaseAsset: ushort.MaxValue,
            TechLevel: 5,
            Cost: 100,
            MaxStack: 20,
            EffectUsage: 0,
            Flags: 0xAABBCCDD,
            new Net7ItemTemplateSharedString(System.Text.Encoding.Latin1.GetBytes("Synthetic Session Item")),
            new Net7ItemTemplateSharedString(Array.Empty<byte>()),
            new Net7ItemTemplateSharedString(Array.Empty<byte>()));
        return Net7ItemTemplateWire.Encode(template);
    }

    private static byte[] BuildFile(params (uint Id, byte[] Payload)[] records)
    {
        using var data = new MemoryStream();
        var offsets = new List<(uint Id, uint Offset)>();
        foreach ((uint id, byte[] payload) in records)
        {
            offsets.Add((id, checked((uint)data.Length)));
            data.Write(EncodeBlock(payload));
        }

        using var file = new MemoryStream();
        byte[] raw4 = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(raw4, checked((uint)data.Length));
        file.Write(EncodeBlock(raw4));
        data.Position = 0;
        data.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(raw4, checked((uint)records.Length));
        file.Write(EncodeBlock(raw4));
        foreach ((uint id, uint offset) in offsets)
        {
            byte[] entry = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(0, 4), id);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4, 4), offset);
            file.Write(EncodeBlock(entry));
        }
        return file.ToArray();
    }

    private static int GetIndexOffset(ReadOnlySpan<byte> file)
    {
        Span<byte> decoded4 = stackalloc byte[4];
        Net7CDataCatalog.DecodeBlock(file[..4], decoded4);
        int dataLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(decoded4));
        return checked(8 + dataLength);
    }

    private static void RewriteIndexEntry(byte[] file, int indexOffset, int index, uint id, uint offset)
    {
        byte[] decoded = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(decoded.AsSpan(0, 4), id);
        BinaryPrimitives.WriteUInt32LittleEndian(decoded.AsSpan(4, 4), offset);
        byte[] encoded = EncodeBlock(decoded);
        encoded.AsSpan().CopyTo(file.AsSpan(indexOffset + index * 8, 8));
    }

    private static byte[] EncodeBlock(ReadOnlySpan<byte> decoded)
    {
        byte[] encoded = new byte[decoded.Length];
        for (int i = 0; i < decoded.Length; i++)
            encoded[decoded.Length - 1 - i] = unchecked((byte)(decoded[i] - i - 0x6A));
        return encoded;
    }
}
