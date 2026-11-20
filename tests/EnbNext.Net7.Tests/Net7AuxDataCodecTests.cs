using System.Buffers.Binary;
using EnbNext.Net7.World;

namespace EnbNext.Net7.Tests;

public sealed class Net7AuxDataCodecTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncomingShortScalarKeepsCompletedPrefixAndNextUpdate(bool objectDelta)
    {
        for (int bytes = 0; bytes < 4; bytes++)
        {
            var root = new Net7AuxDataProperty(0, "Fixture", Net7AuxDataValueKind.Aggregate, 2);
            var first = new Net7AuxDataProperty(1, "Fixture.First", Net7AuxDataValueKind.Int32LittleEndian, 0);
            var last = new Net7AuxDataProperty(2, "Fixture.Last", Net7AuxDataValueKind.Int32LittleEndian, 0);
            root.AddChild(first); root.AddChild(last);
            var schema = new Net7AuxDataSchema("Fixture", [root, first, last]);
            first.Value = 7; last.Value = 8;
            byte[] complete = [1, 0x32, 42, 0, 0, 0, 99, 0, 0, 0];
            var codec = new Net7AuxDataCodec();
            if (objectDelta) codec.ApplyObjectDelta(schema, complete.AsSpan(0, 6 + bytes), default);
            else codec.ApplyIncomingDelta(schema, complete.AsSpan(0, 6 + bytes), default);
            Assert.Equal(42, first.Value);
            Assert.Equal(1UL, first.UpdateRevision);
            Assert.Equal(8, last.Value);
            Assert.Equal(0UL, last.UpdateRevision);
            codec.ApplyIncomingDelta(schema, complete, default);
            Assert.Equal(99, last.Value);
            Assert.Equal(2UL, first.UpdateRevision);
            Assert.Equal(1UL, last.UpdateRevision);
        }
    }

    [Theory]
    [InlineData(999999u, 0)]
    [InlineData(uint.MaxValue, 0)]
    [InlineData(999999u, 1)]
    [InlineData(uint.MaxValue, 1)]
    [InlineData(999999u, 2)]
    [InlineData(uint.MaxValue, 2)]
    public void UndefinedPropertyKeepsCompletedStateAndAcceptsLaterPackets(uint unknownId, int operation)
    {
        var world = new Net7WorldState();
        var obj = world.MaterializeCreate(new Net7CreateUpdate(99, 0, 1, 0, 0, 0, 0));
        var source = new Net7AuxDataSchemaCatalog().CreateObjectSchema(0)!;
        var name = source.Resolve(1);
        name.Value = new Net7AuxDataString(System.Text.Encoding.Latin1.GetBytes("Retained"));
        var codec = new Net7AuxDataCodec();
        // Insert the unknown ID after the completed name, in each of the three
        // native operation lists. No later bytes may be mistaken for a field.
        byte[] nameBytes = [8, 0, .. System.Text.Encoding.Latin1.GetBytes("Retained")];
        byte[] delta = BuildDefault(valueUpdates: operation == 0
            ? [(1u, nameBytes), (unknownId, new byte[4])]
            : [(1u, nameBytes)], stateTwo: operation == 1 ? [unknownId] : [],
            stateZero: operation == 2 ? [unknownId] : []);
        Assert.Null(Apply(delta).AuxDataWarning);
        Assert.Equal("Retained", obj.Name);
        ulong revision = obj.AuxData!.Resolve(1).UpdateRevision;
        name.Value = new Net7AuxDataString(System.Text.Encoding.Latin1.GetBytes("Following"));
        Assert.Null(Apply(codec.Encode(source, [new(1, Net7AuxDataOperationKind.ValueUpdate)])).AuxDataWarning);
        Assert.Equal("Following", obj.Name);
        Assert.True(obj.AuxData.Resolve(1).UpdateRevision > revision);

        Net7WorldApplyResult Apply(byte[] bytes)
        {
            byte[] payload = new byte[6 + bytes.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, 99);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), (ushort)bytes.Length);
            bytes.CopyTo(payload, 6);
            return world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x001B, payload), 0);
        }
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(uint.MaxValue)]
    public void UndefinedPlayerVarSelectorPreservesStateAndDoesNotPreventNextPacket(uint selector)
    {
        var world = new Net7WorldState();
        var field = world.PlayerVar0AuxData.Resolve(503);
        object? before = field.Value;
        ulong revision = field.UpdateRevision;
        byte[] delta = BuildDefault(valueUpdates: [(503u, BitConverter.GetBytes(1234))]);
        byte[] payload = new byte[6 + delta.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, selector);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), (ushort)delta.Length);
        delta.CopyTo(payload, 6);
        Assert.Null(world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x001C, payload), 0).AuxDataWarning);
        Assert.Equal(before, field.Value);
        Assert.Equal(revision, field.UpdateRevision);
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0);
        Assert.Null(world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x001C, payload), 0).AuxDataWarning);
        Assert.Equal(1234, Assert.IsType<Net7AuxDataTime>(field.Value).RelativeValue);
        Assert.True(field.UpdateRevision > revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShipMetadataUsesCurrentCursorAndIgnoresLookalikeTail(bool appendTail)
    {
        var schema = Net7AuxDataSchemaCatalog.Clone(new Net7AuxDataSchemaCatalog().Ship);
        var codec = new Net7AuxDataCodec();
        byte[] maps = new byte[8];
        maps[0] = 2;
        for (int child = 47; child <= 52; child++)
            maps[(4 + child) / 8] |= (byte)(1 << ((4 + child) % 8));
        byte[] metadata = Metadata(10, "First");
        byte[] delta = appendTail ? [1, .. maps, .. metadata, .. Metadata(99, "Later")]
            : [1, .. maps, .. metadata];
        codec.ApplyIncomingDelta(schema, delta, default);
        Assert.Equal(10, schema.Resolve(1922).Value);
        Assert.Equal("First", Assert.IsType<Net7AuxDataString>(schema.Resolve(1923).Value).Text);
        Assert.Equal("Rank", Assert.IsType<Net7AuxDataString>(schema.Resolve(1925).Value).Text);

        static byte[] Metadata(int engine, string guild)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(engine);
            writer.Write((ushort)guild.Length);
            writer.Write(System.Text.Encoding.Latin1.GetBytes(guild));
            writer.Write(4);
            writer.Write((ushort)4);
            writer.Write(System.Text.Encoding.Latin1.GetBytes("Rank"));
            writer.Write(new byte[24]);
            return stream.ToArray();
        }
    }

    [Theory]
    [InlineData(0x001B)]
    [InlineData(0x001C)]
    public void EmptyIncomingAuxViewKeepsStateAndContinues(int opcode)
    {
        var world = new Net7WorldState();
        var schema = opcode == 0x001B ? world.LocalPlayerAuxData : world.PlayerVar0AuxData;
        var field = schema.Resolve(opcode == 0x001B ? 1 : 503);
        object? before = field.Value;
        ulong revision = field.UpdateRevision;
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode((ushort)opcode, new byte[6]), 0);
        Assert.Equal(before, field.Value);
        Assert.Equal(revision, field.UpdateRevision);
        var header = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 8);
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode((ushort)opcode, header), 0);
        Assert.Equal(before, field.Value);
        Assert.Equal(revision, field.UpdateRevision);
    }

    [Theory]
    [InlineData(1, long.MinValue)]
    [InlineData(-2, long.MaxValue)]
    [InlineData(0, long.MinValue)]
    public void TimeSubtractionWrapsWithoutDiscardingFollowingValues(int wire, long timeBase)
    {
        var schema = Net7AuxDataSchemaCatalog.Clone(new Net7AuxDataSchemaCatalog().Ship);
        var codec = new Net7AuxDataCodec();
        codec.ApplyIncomingDelta(schema,
            BuildDefault(valueUpdates: [(1930u, BitConverter.GetBytes(wire)), (1931u, BitConverter.GetBytes(.5f))]),
            new Net7AuxDataCodecContext(timeBase));
        Assert.Equal(new Net7AuxDataTime(wire == 0 ? 0 : unchecked((long)wire - timeBase)), schema.Resolve(1930).Value);
        Assert.Equal(.5f, schema.Resolve(1931).Value);
    }

    [Fact]
    public void DefaultDecoderAllowsRepeatedOperationsBeyondSchemaSize()
    {
        var root = new Net7AuxDataProperty(0, "Fixture", Net7AuxDataValueKind.Aggregate, 1);
        var field = new Net7AuxDataProperty(1, "Fixture.Value", Net7AuxDataValueKind.Int32LittleEndian, 0);
        root.AddChild(field);
        var schema = new Net7AuxDataSchema("Fixture", [root, field]);
        var codec = new Net7AuxDataCodec();
        codec.ApplyIncomingDelta(schema, BuildDefault(valueUpdates:
            [(1u, BitConverter.GetBytes(1)), (1u, BitConverter.GetBytes(2)), (1u, BitConverter.GetBytes(3))],
            stateTwo: [1, 1, 1], stateZero: [1, 1, 1]), default);
        Assert.Equal(3, field.Value);
        Assert.Equal(0, field.State);
        Assert.Equal(6UL, field.UpdateRevision);
        // Native loops compare signed counts; a negative count performs no operations.
        codec.ApplyIncomingDelta(schema, [0, 255,255,255,255, 255,255,255,255, 255,255,255,255], default);
        Assert.Equal(3, field.Value);
        Assert.Equal(6UL, field.UpdateRevision);
    }

    [Fact]
    public void PlayerVarIncomingUpdateRetainsCompletedValuesAndIgnoresTail()
    {
        var world = new Net7WorldState();
        byte[] partial = BuildDefault(valueUpdates:
            [(503u, BitConverter.GetBytes(1234)), (503u, new byte[] { 1, 2, 3 })]);
        // Remove the later operation counts: stop inside the second value.
        partial = partial[..^8];
        Apply(partial);
        Assert.Equal(1234, Assert.IsType<Net7AuxDataTime>(world.PlayerVar0AuxData.Resolve(503).Value).RelativeValue);
        byte[] complete = BuildDefault(valueUpdates: [(503u, BitConverter.GetBytes(5678))]);
        Apply([.. complete, 0xFF]);
        Assert.Equal(5678, Assert.IsType<Net7AuxDataTime>(world.PlayerVar0AuxData.Resolve(503).Value).RelativeValue);
        void Apply(byte[] delta)
        {
            byte[] payload = new byte[6 + delta.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), checked((ushort)delta.Length));
            delta.CopyTo(payload, 6);
            world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x001C, payload), 0);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IncomingCompleteUpdateIgnoresUnusedTail(bool objectDelta, bool compressed)
    {
        var root = new Net7AuxDataProperty(0, "Fixture", Net7AuxDataValueKind.Aggregate, 1);
        var field = new Net7AuxDataProperty(1, "Fixture.Value", Net7AuxDataValueKind.Int32LittleEndian, 0);
        root.AddChild(field);
        var schema = new Net7AuxDataSchema("Fixture", [root, field]);
        field.Value = 7;
        byte[] complete = compressed ? [1, 0x12, 42, 0, 0, 0]
            : BuildDefault(valueUpdates: [(1u, new byte[] { 42, 0, 0, 0 })]);
        byte[] withTail = [.. complete, 0xFF, 0x03, 0x91];
        var codec = new Net7AuxDataCodec();
        if (objectDelta) Assert.Null(codec.ApplyObjectDelta(schema, withTail, default));
        else codec.ApplyIncomingDelta(schema, withTail, default);
        Assert.Equal(42, field.Value);
        Assert.Equal(1UL, field.UpdateRevision);
        codec.ApplyIncomingDelta(schema, [1, 0x12, 99, 0, 0, 0], default);
        Assert.Equal(99, field.Value);
        Assert.Equal(2UL, field.UpdateRevision);
        Assert.Throws<InvalidDataException>(() => codec.Apply(schema, withTail));
        Assert.Equal(99, field.Value);
        Assert.Equal(2UL, field.UpdateRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncomingShortNestedMaskRetainsPrefixAndNextPacketStillApplies(bool objectDelta)
    {
        var root = new Net7AuxDataProperty(0, "Fixture", Net7AuxDataValueKind.Aggregate, 2);
        var first = new Net7AuxDataProperty(1, "Fixture.First", Net7AuxDataValueKind.Int32LittleEndian, 0);
        var nested = new Net7AuxDataProperty(2, "Fixture.Nested", Net7AuxDataValueKind.Aggregate, 114);
        root.AddChild(first); root.AddChild(nested);
        var properties = new List<Net7AuxDataProperty> { root, first, nested };
        for (int i = 0; i < 114; i++)
        {
            var leaf = new Net7AuxDataProperty(i + 3, $"Fixture.Nested.{i}", Net7AuxDataValueKind.Int32LittleEndian, 0);
            nested.AddChild(leaf); properties.Add(leaf);
        }
        var schema = new Net7AuxDataSchema("Fixture", properties);
        first.Value = 7; nested.Children[0].Value = 8;
        // Selector, both root routes, complete first value, then 12 of 15 mask bytes.
        byte[] shortUpdate = new byte[18];
        shortUpdate[0] = 1; shortUpdate[1] = 0x32;
        BinaryPrimitives.WriteInt32LittleEndian(shortUpdate.AsSpan(2), 42);
        shortUpdate[6] = 0x12;
        var codec = new Net7AuxDataCodec();
        if (objectDelta)
        {
            var note = codec.ApplyObjectDelta(schema, shortUpdate, default, default, out bool compatible);
            Assert.True(compatible); Assert.Contains("need 15 byte(s), have 12", note);
        }
        else codec.ApplyIncomingDelta(schema, shortUpdate, default);
        Assert.Equal(42, first.Value); Assert.Equal(1UL, first.UpdateRevision);
        Assert.Equal(8, nested.Children[0].Value); Assert.Equal(0UL, nested.Children[0].UpdateRevision);
        byte[] complete = new byte[25]; shortUpdate.CopyTo(complete, 0);
        BinaryPrimitives.WriteInt32LittleEndian(complete.AsSpan(21), 99);
        codec.ApplyIncomingDelta(schema, complete, default);
        Assert.Equal(99, nested.Children[0].Value);
        Assert.Equal(2UL, first.UpdateRevision);
        Assert.Equal(1UL, nested.Children[0].UpdateRevision);
    }

    [Fact]
    public void ProjectionRevisionTracksValueStateAndRollbackIndependentlyOfNativeCallbacks()
    {
        var schema=new Net7AuxDataSchemaCatalog().Ship;
        var legos=schema.Resolve(1254);var leaf=schema.Resolve(1260);var unrelated=schema.Resolve(29);
        ulong before=legos.MaterializedRevision;
        leaf.Value=12;Assert.True(legos.MaterializedRevision>before);Assert.Equal(0UL,leaf.UpdateRevision);
        before=legos.MaterializedRevision;leaf.State=2;Assert.True(legos.MaterializedRevision>before);
        before=legos.MaterializedRevision;leaf.State=0;Assert.True(legos.MaterializedRevision>before);
        before=legos.MaterializedRevision;unrelated.Value=(byte)1;Assert.Equal(before,legos.MaterializedRevision);
        leaf.UpdateRevision=4;before=legos.MaterializedRevision;leaf.UpdateRevision=0;
        Assert.True(legos.MaterializedRevision>before);Assert.Equal(0UL,leaf.UpdateRevision);
        before=legos.MaterializedRevision;leaf.Value=null;Assert.True(legos.MaterializedRevision>before);
    }

    [Fact]
    public void World11EmbeddedSchemasHaveExactNodeCountsAndCriticalPaths()
    {
        var catalog = new Net7AuxDataSchemaCatalog();

        Assert.Equal(5596, catalog.LocalPlayer.Properties.Count);
        Assert.Equal(1176, catalog.PlayerVar0.Properties.Count);
        Assert.Equal(1933, catalog.Ship.Properties.Count);
        Assert.Equal("LocalPlayerAuxData.RPGInfo.Skills.41.LastActivation", catalog.LocalPlayer.Resolve(Net7AuxDataPropertyIds.LocalPlayerSkill41LastActivation).Path);
        Assert.Equal(Net7AuxDataValueKind.Time32LittleEndianRelative, catalog.LocalPlayer.Resolve(Net7AuxDataPropertyIds.LocalPlayerSkill41LastActivation).Kind);
        Assert.Equal("LocalPlayerAuxData.RPGInfo.SkillAbilities.0.Validity", catalog.LocalPlayer.Resolve(Net7AuxDataPropertyIds.LocalPlayerSkillAbility0Validity).Path);
        Assert.Equal("LocalPlayerAuxData.RPGInfo.SkillAbilities.28.Validity", catalog.LocalPlayer.Resolve(Net7AuxDataPropertyIds.LocalPlayerSkillAbility28Validity).Path);
        Assert.Equal("LocalPlayerAuxData.GroupInfo", catalog.LocalPlayer.Resolve(5559).Path);
    }

    [Theory]
    [InlineData(0x00, "ShipAuxData", 1933)]
    [InlineData(0x01, "ShipAuxData", 1933)]
    [InlineData(0x03, "Planet", 2)]
    [InlineData(0x04, "Component", 2)]
    [InlineData(0x08, "Light", 8)]
    [InlineData(0x0B, "Stargate", 2)]
    [InlineData(0x0C, "Starbase", 2)]
    [InlineData(0x19, "HulkAuxData", 703)]
    [InlineData(0x22, "Decoration", 4)]
    [InlineData(0x25, "Decoration", 4)]
    [InlineData(0x29, "Decoration", 4)]
    [InlineData(0x26, "Harvestable", 445)]
    [InlineData(0x27, "ManufacturingLab", 2427)]
    public void CurrentRetailObjectAuxDataFactoryHasEveryProvenSchema(int objectType, string name, int count)
    {
        var catalog = new Net7AuxDataSchemaCatalog();
        Net7AuxDataSchema schema = Assert.IsType<Net7AuxDataSchema>(catalog.CreateObjectSchema(checked((byte)objectType)));

        Assert.Equal(name, schema.Name);
        Assert.Equal(count, schema.Properties.Count);
        Net7AuxDataProperty firstChild = Assert.Single(schema.Root.Children, child => child.ChildIndex == 0);
        Assert.EndsWith(".Name", firstChild.Path, StringComparison.Ordinal);
        Assert.Equal(Net7AuxDataValueKind.LengthPrefixedString, firstChild.Kind);
    }

    [Fact]
    public void ManufacturingLabAndHarvestableSchemasMatchClosedNestedLayouts()
    {
        var catalog = new Net7AuxDataSchemaCatalog();
        Net7AuxDataSchema manufacturing = catalog.ManufacturingLab;
        Net7AuxDataSchema harvestable = catalog.Harvestable;

        Assert.Equal(Net7AuxDataValueKind.Int32LittleEndian,
            Assert.Single(manufacturing.Properties, p => p.Path == "ManufacturingLab.Mode").Kind);
        Assert.Equal(Net7AuxDataValueKind.UInt64LittleEndian,
            Assert.Single(manufacturing.Properties, p => p.Path == "ManufacturingLab.NegotiatedCost").Kind);
        Assert.Equal(Net7AuxDataValueKind.Int32LittleEndian,
            Assert.Single(manufacturing.Properties, p => p.Path == "ManufacturingLab.ManufactureComponents.0.ItemTemplateID").Kind);
        Assert.Equal(Net7AuxDataValueKind.LengthPrefixedString,
            Assert.Single(manufacturing.Properties, p => p.Path == "ManufacturingLab.KnownFormula.499.ItemName").Kind);
        Assert.Equal(Net7AuxDataValueKind.BooleanByte,
            Assert.Single(manufacturing.Properties, p => p.Path == "ManufacturingLab.PrimaryCategories.1.Categories.4.Subcategories.4.Visible").Kind);

        // Constructor evidence is wire-order sensitive here: a category
        // record stores ID before Subcategories in memory but registers children
        // as Name, Subcategories, ID. These child indexes/property IDs follow
        // registration order.
        Net7AuxDataProperty category = Assert.Single(
            manufacturing.Properties,
            p => p.Path == "ManufacturingLab.PrimaryCategories.0.Categories.0");
        Assert.Equal(
            new[]
            {
                "ManufacturingLab.PrimaryCategories.0.Categories.0.Name",
                "ManufacturingLab.PrimaryCategories.0.Categories.0.Subcategories",
                "ManufacturingLab.PrimaryCategories.0.Categories.0.ID",
            },
            category.Children.Select(child => child.Path).ToArray());

        Assert.Equal(Net7AuxDataValueKind.Int32LittleEndian,
            Assert.Single(harvestable.Properties, p => p.Path == "Harvestable.HarvestableResources.39.ItemTemplateID").Kind);
        Assert.Equal(Net7AuxDataValueKind.Float32LittleEndian,
            Assert.Single(harvestable.Properties, p => p.Path == "Harvestable.PercentFull").Kind);
        Assert.Equal(Net7AuxDataValueKind.Int32LittleEndian,
            Assert.Single(harvestable.Properties, p => p.Path == "Harvestable.TechLevel").Kind);
    }

    [Fact]
    public void ProxyCompactHarvestableAndDecorationFixturesDecodeAgainstCurrentFactorySchemas()
    {
        var catalog = new Net7AuxDataSchemaCatalog();
        var codec = new Net7AuxDataCodec();

        Net7AuxDataSchema harvestable = Net7AuxDataSchemaCatalog.Clone(catalog.Harvestable);
        byte[] harvestName = [1, 0x12, 4, 0, (byte)'R', (byte)'o', (byte)'c', (byte)'k'];
        codec.Apply(harvestable, harvestName);
        Assert.Equal("Rock", Assert.IsType<Net7AuxDataString>(harvestable.Resolve(1).Value).Text);

        Net7AuxDataSchema decoration = Net7AuxDataSchemaCatalog.Clone(catalog.Decoration);
        using var fixture = new MemoryStream();
        fixture.Write(new byte[] { 1, 0x72, 3, 0, (byte)'N', (byte)'a', (byte)'v', 1 });
        Span<byte> scalar = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(scalar, BitConverter.SingleToInt32Bits(900f));
        fixture.Write(scalar);
        codec.Apply(decoration, fixture.ToArray());
        Assert.Equal("Nav", Assert.IsType<Net7AuxDataString>(decoration.Resolve(1).Value).Text);
        Assert.Equal((byte)1, Assert.IsType<byte>(decoration.Resolve(2).Value));
        Assert.Equal(900f, Assert.IsType<float>(decoration.Resolve(3).Value));
    }

    [Theory]
    [InlineData("FEB53C44")]
    [InlineData("82943E44")]
    [InlineData("00000000")]
    public void ShipInterruptibleActivationUsesFourWireBytesAndPreservesFollowingValue(string hex)
    {
        var schema = Net7AuxDataSchemaCatalog.Clone(new Net7AuxDataSchemaCatalog().Ship);
        var codec = new Net7AuxDataCodec();
        var bytes = Convert.FromHexString(hex);
        var context = new Net7AuxDataCodecContext(0x1000);
        // The report supplies the value suffix, not a complete captured packet.
        codec.Apply(schema, BuildDefault(valueUpdates: [(1930u, bytes), (1931u, BitConverter.GetBytes(.5f))]), context);
        int wire = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        Assert.Equal(new Net7AuxDataTime(wire == 0 ? 0 : (long)wire - context.TimeBase), schema.Resolve(1930).Value);
        Assert.Equal(.5f, schema.Resolve(1931).Value);
        codec.Apply(schema, BuildDefault(valueUpdates: [(1930u, bytes)]), context);
        Assert.Throws<InvalidDataException>(() => codec.Apply(schema, BuildDefault(valueUpdates: [(1930u, bytes[..3])]), context));
    }

    [Fact]
    public void DefaultCodecValueStateTwoStateZeroPreservesScalarValue()
    {
        Net7AuxDataSchema schema = Net7AuxDataSchemaCatalog.Clone(new Net7AuxDataSchemaCatalog().LocalPlayer);
        var codec = new Net7AuxDataCodec();
        var context = new Net7AuxDataCodecContext(0x1000);
        byte[] wireValue = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(wireValue, 0x1234);

        codec.Apply(schema, BuildDefault(valueUpdates: [(0x0B59u, wireValue)]), context);
        Net7AuxDataProperty property = schema.Resolve(Net7AuxDataPropertyIds.LocalPlayerSkill41LastActivation);
        Assert.Equal(2, property.State);
        Assert.Equal(new Net7AuxDataTime(0x234), Assert.IsType<Net7AuxDataTime>(property.Value));

        codec.Apply(schema, BuildDefault(stateTwo: [0x0B59u]), context);
        Assert.Equal(2, property.State);
        Assert.Equal(new Net7AuxDataTime(0x234), Assert.IsType<Net7AuxDataTime>(property.Value));

        codec.Apply(schema, BuildDefault(stateZero: [0x0B59u]), context);
        Assert.Equal(0, property.State);
        Assert.Equal(new Net7AuxDataTime(0x234), Assert.IsType<Net7AuxDataTime>(property.Value));
    }

    [Fact]
    public void CompressedDirectAggregateStateOperationsRecurseAndNormalize()
    {
        Net7AuxDataSchema schema = Net7AuxDataSchemaCatalog.Clone(new Net7AuxDataSchemaCatalog().PlayerVar0);
        var codec = new Net7AuxDataCodec();

        codec.Apply(schema, [1, 0x05]);
        Assert.Equal(2, schema.Root.State);
        Assert.Equal(2, schema.Resolve(1).State);
        Assert.Equal(2, schema.Resolve(1175).State);

        Net7AuxDataCodec.Normalize(schema.Root);
        Assert.Equal(1, schema.Root.State);
        Assert.Equal(1, schema.Resolve(1).State);

        codec.Apply(schema, [1, 0x09]);
        Assert.Equal(0, schema.Root.State);
        Assert.Equal(0, schema.Resolve(1).State);
    }

    [Fact]
    public void CompressedZeroChildMapConsumesOneByteAsRetailNoOp()
    {
        Net7AuxDataSchema schema = Net7AuxDataSchemaCatalog.Clone(new Net7AuxDataSchemaCatalog().Ship);
        var codec = new Net7AuxDataCodec();

        // Retail 0x004A9710 reads four logical header bits. With direct bit 0
        // and maps 1..3 all clear, 0x004A9400 still reads the four-bit bitset
        // (one encoded byte), then performs no child operation and succeeds.
        codec.Apply(schema, [1, 0x00]);

        Assert.All(schema.Properties, static property => Assert.Equal(0, property.State));
    }

    [Fact]
    public void CompressedDirectAggregateWithoutOperationIsRetailNoOp()
    {
        Net7AuxDataSchema schema = Net7AuxDataSchemaCatalog.Clone(new Net7AuxDataSchemaCatalog().Ship);
        var codec = new Net7AuxDataCodec();

        // Current retail consumes direct header 0x01, reports a diagnostic
        // assertion because no operation bit is set, then returns successfully
        // without mutating the aggregate or setting the decoder failure flag.
        codec.Apply(schema, [1, 0x01]);

        Assert.All(schema.Properties, static property => Assert.Equal(0, property.State));
    }

    [Fact]
    public void CompressedNestedDirectAggregateWithoutOperationIsRetailNoOp()
    {
        Net7AuxDataSchema schema = Net7AuxDataSchemaCatalog.Clone(new Net7AuxDataSchemaCatalog().Ship);
        var codec = new Net7AuxDataCodec();

        // Root child index 4 is an aggregate. Route to it through the root
        // value map, then feed the nested aggregate the retail-tolerated direct
        // no-op header 0x01. The nested visitor consumes one byte and returns.
        byte[] delta = new byte[1 + 8 + 1];
        delta[0] = 1;
        delta[1] = 0x02;
        delta[2] = 0x01; // bit 8 = child index 4 in the root value map
        delta[^1] = 0x01;

        codec.Apply(schema, delta);

        Assert.All(schema.Properties, static property => Assert.Equal(0, property.State));
    }

    [Fact]
    public void CompressedNestedZeroChildMapIsAcceptedAfterAggregateRoute()
    {
        Net7AuxDataSchema schema = Net7AuxDataSchemaCatalog.Clone(new Net7AuxDataSchemaCatalog().Ship);
        var codec = new Net7AuxDataCodec();

        // Root has 58 children. Header 0x02 enables the value map, making the
        // record 4 + 58 = 62 bits = 8 bytes. Child index 4 is EnergyPercent,
        // an aggregate, so bit 8 routes into its nested record. The nested 0x00
        // is a valid retail no-op aggregate record and consumes exactly one byte.
        byte[] delta = new byte[1 + 8 + 1];
        delta[0] = 1;
        delta[1] = 0x02;
        delta[2] = 0x01; // bit 8 = child index 4 in the root value map
        delta[^1] = 0x00;

        codec.Apply(schema, delta);

        Assert.All(schema.Properties, static property => Assert.Equal(0, property.State));
    }

    [Fact]
    public void CurrentServerTerminalEmptyGroupMembersRouteIsRetailCompatibleNoOp()
    {
        Net7AuxDataSchema schema = Net7AuxDataSchemaCatalog.Clone(new Net7AuxDataSchemaCatalog().LocalPlayer);
        var codec = new Net7AuxDataCodec();

        // Current sector-entry full-state packets route root child 17
        // (GroupInfo), then GroupInfo child 10 (Members), and end without an
        // encoded record for that empty fixed collection. This is the minimal
        // form of the v2026-09-04 capture boundary.
        byte[] delta =
        [
            1,
            0x02, 0x00, 0x20,
            0x02, 0x40,
        ];

        codec.Apply(schema, delta);

        Assert.All(schema.Properties, static property => Assert.Equal(0, property.State));
    }

    [Fact]
    public void InvalidPropertyIdAndTruncatedValueFailClosed()
    {
        Net7AuxDataSchema schema = Net7AuxDataSchemaCatalog.Clone(new Net7AuxDataSchemaCatalog().LocalPlayer);
        var codec = new Net7AuxDataCodec();

        Assert.Throws<InvalidDataException>(() => codec.Apply(schema, BuildDefault(valueUpdates: [(999999u, new byte[4])])));
        Assert.Throws<InvalidDataException>(() => codec.Apply(schema, BuildDefault(valueUpdates: [(0x0B59u, new byte[3])])));
    }

    private static byte[] BuildDefault(
        (uint Id, byte[] Value)[]? valueUpdates = null,
        uint[]? stateTwo = null,
        uint[]? stateZero = null)
    {
        valueUpdates ??= [];
        stateTwo ??= [];
        stateZero ??= [];
        using var stream = new MemoryStream();
        stream.WriteByte(0);
        WriteU32(stream, checked((uint)valueUpdates.Length));
        foreach ((uint id, byte[] value) in valueUpdates)
        {
            WriteU32(stream, id);
            stream.Write(value);
        }
        WriteU32(stream, checked((uint)stateTwo.Length));
        foreach (uint id in stateTwo)
            WriteU32(stream, id);
        WriteU32(stream, checked((uint)stateZero.Length));
        foreach (uint id in stateZero)
            WriteU32(stream, id);
        return stream.ToArray();
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
