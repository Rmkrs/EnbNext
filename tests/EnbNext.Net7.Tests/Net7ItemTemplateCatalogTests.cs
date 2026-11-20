using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Tests;

public sealed class Net7ItemTemplateCatalogTests
{
    [Fact]
    public void MaterializationIsKeyedByItemTemplateIdAndConsistentDuplicateKeepsFirstDefinition()
    {
        Net7ItemTemplate first = Net7ItemTemplateWire.Decode(Net7CDataCatalogTests.BuildSessionItemTemplatePayload());
        var catalog = new Net7ItemTemplateCatalog();
        Assert.Same(first, catalog.Materialize(first));
        Net7ItemTemplate equivalent = first with
        {
            Cost = first.Cost + 1,
            Name = new Net7ItemTemplateSharedString(first.Name.WireBytes.ToArray()),
        };
        Assert.Same(first, catalog.Materialize(equivalent));
        Assert.Equal(1, catalog.Count);
        Assert.True(catalog.TryGet(first.ItemTemplateId, out Net7ItemTemplate? resolved));
        Assert.Same(first, resolved);
    }

    [Fact]
    public void ConflictingDuplicateKeepsFirstDefinitionAndLaterItemsStillMaterialize()
    {
        Net7ItemTemplate first = Net7ItemTemplateWire.Decode(Net7CDataCatalogTests.BuildSessionItemTemplatePayload());
        var catalog = new Net7ItemTemplateCatalog();
        catalog.Materialize(first);
        Assert.Same(first, catalog.Materialize(first with { ItemType = first.ItemType + 1 }));
        Assert.Same(first, catalog.Materialize(first with
        {
            Name = new Net7ItemTemplateSharedString(System.Text.Encoding.Latin1.GetBytes("Different Name")),
        }));
        var next = first with { ItemTemplateId = first.ItemTemplateId + 1 };
        Assert.Same(next, catalog.Materialize(next));
        Assert.Equal(2, catalog.Count);
        Assert.True(catalog.TryGet(first.ItemTemplateId, out var retained));
        Assert.Same(first, retained);
    }

    [Fact]
    public void CDataMaterializationUsesEmbeddedIdentityAndPreservesMissingState()
    {
        var catalog = new Net7ItemTemplateCatalog();
        Assert.Null(catalog.MaterializeCDataRecord(123, null));
        Assert.Equal(0, catalog.Count);

        byte[] payload = Net7CDataCatalogTests.BuildSessionItemTemplatePayload();
        var first = catalog.MaterializeCDataRecord(123, new Net7CDataRecord(456, payload));
        Assert.NotNull(first);
        Assert.True(catalog.TryGet(first.ItemTemplateId, out var retained));
        Assert.Same(first, retained);
        Assert.False(catalog.TryGet(123, out _));
        Assert.False(catalog.TryGet(456, out _));

        byte[] nextPayload = payload.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(nextPayload.AsSpan(0, 4), 0x55667788u);
        var next = catalog.MaterializeCDataRecord(123, new Net7CDataRecord(123, nextPayload));
        Assert.NotNull(next);
        Assert.Equal(0x55667788u, next.ItemTemplateId);
        Assert.True(catalog.TryGet(next.ItemTemplateId, out var second));
        Assert.Same(next, second);
        Assert.Null(catalog.MaterializeCDataRecord(123, null));
        Assert.Equal(2, catalog.Count);
        Assert.True(catalog.TryGet(first.ItemTemplateId, out retained));
        Assert.Same(first, retained);
    }

}
