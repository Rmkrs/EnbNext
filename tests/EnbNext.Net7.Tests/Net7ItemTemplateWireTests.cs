using System.Buffers.Binary;
using System.Text;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.LocalData;

namespace EnbNext.Net7.Tests;

public sealed class Net7ItemTemplateWireTests
{
    [Fact]
    public void PinnedCurrentCDataAll5530RecordsDecodeAndRoundTripByteExact()
    {
        Net7CDataCatalog catalog = Net7LocalDataAssets.CurrentCData;
        int decoded = 0;
        for (uint id = 507; id <= 7184; id++)
        {
            if (!catalog.TryGetRecord(id, out Net7CDataRecord? record))
                continue;
            Net7CDataRecord value = Assert.IsType<Net7CDataRecord>(record);
            Net7ItemTemplate template = Net7ItemTemplateWire.Decode(value.Payload);
            Assert.Equal(id, template.ItemTemplateId);
            Assert.Equal(value.Payload, Net7ItemTemplateWire.Encode(template));
            decoded++;
        }
        Assert.Equal(5_530, decoded);
    }

    [Fact]
    public void RepresentativeCurrentRecordsPreserveMixedEndianStringsAttributesAndNestedVectors()
    {
        Net7CDataCatalog catalog = Net7LocalDataAssets.CurrentCData;
        Assert.True(catalog.TryGetRecord(507, out Net7CDataRecord? r507));
        Net7ItemTemplate item507 = Net7ItemTemplateWire.Decode(Assert.IsType<Net7CDataRecord>(r507).Payload);
        Assert.Equal(507u, item507.ItemTemplateId);
        Assert.Equal("Dragonclaw Missile", item507.Name.EffectiveText);
        Assert.Contains(item507.Info.Attributes, a => a.Type == Net7ItemTemplateAttributeType.Int);
        Assert.Contains(item507.Info.Attributes, a => a.Type == Net7ItemTemplateAttributeType.String);

        Assert.True(catalog.TryGetRecord(773, out Net7CDataRecord? r773));
        Net7ItemTemplate item773 = Net7ItemTemplateWire.Decode(Assert.IsType<Net7CDataRecord>(r773).Payload);
        Net7ItemTemplateAttribute floatAttribute = Assert.Single(item773.Info.Attributes, a => a.AttributeId == 9);
        Assert.Equal(Net7ItemTemplateAttributeType.Float, floatAttribute.Type);
        Assert.Equal(15.0f, floatAttribute.FloatValue);
        Assert.Empty(item773.Info.VectorA.Entries);
        Net7ItemTemplateVectorEntry entry = Assert.Single(item773.Info.VectorB.Entries);
        Assert.NotNull(item773.Info.VectorB.Metadata);
        Assert.Equal(0u, item773.Info.VectorB.Metadata!.ClampedTimeOrDuration);
        Assert.Equal(0u, entry.Field50);
        Assert.Equal(2u, entry.Field54);
    }

    [Fact]
    public void PacketSharedStringAndItemInfoStringKeepDistinctWireConventions()
    {
        var emptyVector = new Net7ItemTemplateVector(Array.Empty<Net7ItemTemplateVectorEntry>(), null);
        var attributes = new[]
        {
            new Net7ItemTemplateAttribute(2, Net7ItemTemplateAttributeType.Int, 0x11223344, null),
            new Net7ItemTemplateAttribute(9, Net7ItemTemplateAttributeType.Float, unchecked((uint)BitConverter.SingleToInt32Bits(1.5f)), null),
            new Net7ItemTemplateAttribute(11, Net7ItemTemplateAttributeType.String, 0, Encoding.Latin1.GetBytes("attribute\0")),
        };
        var template = new Net7ItemTemplate(
            0x01020304, 5, 6, 7,
            new Net7ItemTemplateInfo(attributes, emptyVector, emptyVector),
            0x1122, 0x3344, 0x5566,
            0x778899AA, 0xBBCCDDEE, 0x12345678, 0x89ABCDEF,
            new Net7ItemTemplateSharedString(new byte[] { (byte)'A', 0, (byte)'B' }),
            new Net7ItemTemplateSharedString(Array.Empty<byte>()),
            new Net7ItemTemplateSharedString(Encoding.Latin1.GetBytes("tail")));

        byte[] payload = Net7ItemTemplateWire.Encode(template);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, payload.AsSpan(0, 4).ToArray());
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4)));
        Net7ItemTemplate decoded = Net7ItemTemplateWire.Decode(payload);
        Assert.Equal("A", decoded.Name.EffectiveText);
        Assert.Equal(new byte[] { (byte)'A', 0, (byte)'B' }, decoded.Name.WireBytes);
        Net7ItemTemplateAttribute stringAttribute = Assert.Single(decoded.Info.Attributes, a => a.AttributeId == 11);
        Assert.Equal("attribute", stringAttribute.StringValue);
        Assert.Equal(0, stringAttribute.StringWireBytes![^1]);
        Assert.Equal(payload, Net7ItemTemplateWire.Encode(decoded));
    }

    [Fact]
    public void AttributeStringUsesFirstNulAndConsumesItsWholeDeclaredLength()
    {
        var source = Net7ItemTemplateWire.Decode(Net7CDataCatalogTests.BuildSessionItemTemplatePayload());
        var vector = new Net7ItemTemplateVector([], null);
        var info = new Net7ItemTemplateInfo(
            [new Net7ItemTemplateAttribute(11, Net7ItemTemplateAttributeType.String, 0, [(byte)'A', 0, 0])],
            vector, vector);
        byte[] payload = Net7ItemTemplateWire.Encode(source with { Info = info });
        // Fixed header, count, attribute ID and length precede the three bytes.
        payload[25] = (byte)'Z';
        var decoded = Net7ItemTemplateWire.Decode(payload);
        var attribute = Assert.Single(decoded.Info.Attributes);
        Assert.Equal("A", attribute.StringValue);
        Assert.Equal(new byte[] { (byte)'A', 0, (byte)'Z' }, attribute.StringWireBytes);
        Assert.Equal(source.GameBasset, decoded.GameBasset);
        Assert.Equal(source.Cost, decoded.Cost);
        Assert.Equal(source.Name.EffectiveText, decoded.Name.EffectiveText);
        var catalog = new Net7ItemTemplateCatalog();
        Assert.Same(decoded, catalog.Materialize(decoded));
        Assert.True(catalog.TryGet(decoded.ItemTemplateId, out var stored));
        Assert.Equal("A", Assert.Single(stored!.Info.Attributes).StringValue);
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x80000000u)]
    public void NegativeVectorCountConsumesMetadataWithoutEntries(uint count)
    {
        var source = Net7ItemTemplateWire.Decode(Net7CDataCatalogTests.BuildSessionItemTemplatePayload());
        var vector = new Net7ItemTemplateVector([], null);
        byte[] valid = Net7ItemTemplateWire.Encode(source with { Info = new Net7ItemTemplateInfo([], vector, vector) });
        byte[] payload = [.. valid.AsSpan(0, 21), .. new byte[16], .. valid.AsSpan(21)];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(17, 4), count);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(21, 4), 17);
        var decoded = Net7ItemTemplateWire.Decode(payload);
        Assert.Empty(decoded.Info.VectorA.Entries);
        Assert.NotNull(decoded.Info.VectorA.Metadata);
        Assert.Equal(17u, decoded.Info.VectorA.Metadata!.ClampedTimeOrDuration);
        Assert.Empty(decoded.Info.VectorB.Entries);
        Assert.Null(decoded.Info.VectorB.Metadata);
        Assert.Equal(source.GameBasset, decoded.GameBasset);
        Assert.Equal(source.Cost, decoded.Cost);
        Assert.Equal(source.Name.EffectiveText, decoded.Name.EffectiveText);
        var catalog = new Net7ItemTemplateCatalog();
        Assert.Same(decoded, catalog.Materialize(decoded));
        Assert.True(catalog.TryGet(source.ItemTemplateId, out _));
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x80000000u)]
    public void NegativeEntryListCountsPreserveFollowingFields(uint count)
    {
        var source = Net7ItemTemplateWire.Decode(Net7CDataCatalogTests.BuildSessionItemTemplatePayload());
        var text = new Net7ItemTemplateSharedString([]);
        var entry = new Net7ItemTemplateVectorEntry(text, text, text, [], [], 123, 456);
        var vector = new Net7ItemTemplateVector([entry], new(17, 0, 0, 0));
        byte[] payload = Net7ItemTemplateWire.Encode(source with
        {
            Info = new Net7ItemTemplateInfo([], vector, new([], null)),
        });
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(27, 4), count);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(31, 4), count);
        var decoded = Net7ItemTemplateWire.Decode(payload);
        var decodedEntry = Assert.Single(decoded.Info.VectorA.Entries);
        Assert.Empty(decodedEntry.ListA);
        Assert.Empty(decodedEntry.ListB);
        Assert.Equal(123u, decodedEntry.Field50);
        Assert.Equal(456u, decodedEntry.Field54);
        Assert.Equal(source.Cost, decoded.Cost);
        Assert.Equal(source.Name.EffectiveText, decoded.Name.EffectiveText);
        var catalog = new Net7ItemTemplateCatalog();
        Assert.Same(decoded, catalog.Materialize(decoded));
        Assert.True(catalog.TryGet(source.ItemTemplateId, out _));
    }

    [Fact]
    public void UnusedTailDoesNotChangeDecodedOrMaterializedDefinition()
    {
        byte[] payload = Net7CDataCatalogTests.BuildSessionItemTemplatePayload();
        var decoded = Net7ItemTemplateWire.Decode([.. payload, 0xFF, 0x01, 0x02]);
        Assert.Equal(payload, Net7ItemTemplateWire.Encode(decoded));
        var catalog = new Net7ItemTemplateCatalog();
        Assert.Same(decoded, catalog.Materialize(decoded));
        Assert.True(catalog.TryGet(decoded.ItemTemplateId, out var stored));
        Assert.Same(decoded, stored);
        var next = decoded with { ItemTemplateId = decoded.ItemTemplateId + 1 };
        var materialized = catalog.Materialize(Net7ItemTemplateWire.Decode(Net7ItemTemplateWire.Encode(next)));
        Assert.Equal(next.ItemTemplateId, materialized.ItemTemplateId);
        Assert.Equal(2, catalog.Count);
    }

    [Fact]
    public void MalformedOrUnknownItemTemplateInfoFailsClosed()
    {
        byte[] valid = Net7CDataCatalogTests.BuildSessionItemTemplatePayload();
        Net7ItemTemplate full = Net7ItemTemplateWire.Decode(valid);
        // 008955C0: everything before the trailing SharedStrings lands in
        // uninitialized storage; the strings themselves start at length zero.
        int trailing = valid.Length - 6 - full.Name.WireBytes.Length - full.StringFieldB.WireBytes.Length - full.StringFieldC.WireBytes.Length;
        for (int length = 0; length < trailing; length++)
            Assert.Throws<InvalidDataException>(() => Net7ItemTemplateWire.Decode(valid.AsSpan(0, length)));
        Net7ItemTemplate bare = Net7ItemTemplateWire.Decode(valid.AsSpan(0, trailing));
        Assert.Equal(full.Cost, bare.Cost);
        Assert.Empty(bare.Name.WireBytes); Assert.Empty(bare.StringFieldB.WireBytes); Assert.Empty(bare.StringFieldC.WireBytes);
        if (full.Name.WireBytes.Length > 1)
        {
            Net7ItemTemplate cut = Net7ItemTemplateWire.Decode(valid.AsSpan(0, trailing + 3));
            Assert.Equal(full.Name.WireBytes[..1], cut.Name.WireBytes);
            Assert.Empty(cut.StringFieldC.WireBytes);
        }

        byte[] unknownAttribute = new byte[17 + 4];
        BinaryPrimitives.WriteUInt32BigEndian(unknownAttribute.AsSpan(0, 4), 1);
        unknownAttribute[16] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(unknownAttribute.AsSpan(17, 4), 0xFFFF);
        Assert.Throws<InvalidDataException>(() => Net7ItemTemplateWire.Decode(unknownAttribute));

        var emptyVector = new Net7ItemTemplateVector(Array.Empty<Net7ItemTemplateVectorEntry>(), null);
        var badString = new Net7ItemTemplate(
            1, 0, 0, 0,
            new Net7ItemTemplateInfo(
                new[] { new Net7ItemTemplateAttribute(11, Net7ItemTemplateAttributeType.String, 0, Encoding.Latin1.GetBytes("missing-nul")) },
                emptyVector, emptyVector),
            0, 0, 0, 0, 0, 0, 0,
            new Net7ItemTemplateSharedString(Array.Empty<byte>()),
            new Net7ItemTemplateSharedString(Array.Empty<byte>()),
            new Net7ItemTemplateSharedString(Array.Empty<byte>()));
        Assert.Throws<InvalidDataException>(() => Net7ItemTemplateWire.Encode(badString));
    }
}
