using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Net7.Protocol;
using EnbNext.Net7.World;

namespace EnbNext.Net7.Tests;

public sealed class Net7WorldWireTests
{
    [Theory]
    [InlineData(0x0008, 48, 8)]
    [InlineData(0x003F, 48, 8)]
    [InlineData(0x0040, 32, 4)]
    [InlineData(0x0046, 64, 8)]
    public void UninitializedMovementShortReadsKeepStateAndFollowingPacketApplies(int opcode, int size, int positionOffset)
    {
        var world = new Net7WorldState();
        var obj = world.MaterializeCreate(new(123, 1, 1, 4, 0, 0, 0));
        byte[] payload = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 123);
        WriteSingle(payload, positionOffset, 17);
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode((ushort)opcode, payload), 100);
        var previous = obj.Spatial;
        Assert.NotNull(previous);
        int drift = world.Time.DriftSampleCount;
        WriteSingle(payload, positionOffset, 42);
        for (int length = 0; length < size; length++)
        {
            var result = world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode((ushort)opcode, payload[..length]), 200);
            Assert.Same(previous, obj.Spatial);
            Assert.Equal(17f, obj.Spatial!.Position.X);
            Assert.Equal(drift, world.Time.DriftSampleCount);
            Assert.Null(result.AuxDataWarning);
            Assert.Null(result.AuxDataFailureReceipt);
            Assert.False(result.RequestTimeDue);
        }
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode((ushort)opcode, payload), 300);
        Assert.Equal(42f, obj.Spatial!.Position.X);
        Assert.Equal(drift + (opcode == 0x0008 ? 1 : 0), world.Time.DriftSampleCount);
    }

    [Theory]
    [InlineData(0u, 0x7fffffffu, 0x7fffffffu)]
    [InlineData(0u, 0x80000000u, 0x7fffffffu)]
    [InlineData(0u, 0xffffffffu, 0x7fffffffu)]
    [InlineData(0xffffffffu, 0u, 0x80000000u)]
    public void SimpleMovementSaturatesCorrectedTimeAndStillAppliesTheUpdate(uint correction, uint timestamp, uint expected)
    {
        var world = new Net7WorldState();
        var obj = world.MaterializeCreate(new(123, 1, 1, 4, 0, 0, 0));
        byte[] sync = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(sync.AsSpan(4), correction);
        BinaryPrimitives.WriteUInt32LittleEndian(sync.AsSpan(8), correction);
        world.Time.ApplySetClientTime(sync, 0);
        byte[] packet = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 123);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), timestamp);
        WriteSingle(packet, 8, 42);
        WriteSingle(packet, 32, 1);
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0008, packet), 100);
        Assert.Equal(expected, obj.Spatial!.ClientTimestamp);
        Assert.Equal(42f, obj.Spatial.Position.X);
        Assert.Equal(1, world.Time.DriftSampleCount);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), correction);
        WriteSingle(packet, 8, 43);
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0008, packet), 101);
        Assert.Equal(0u, obj.Spatial!.ClientTimestamp);
        Assert.Equal(43f, obj.Spatial.Position.X);
        Assert.Equal(2, world.Time.DriftSampleCount);
    }

    [Theory]
    [InlineData(0x003F, 48)]
    [InlineData(0x0046, 64)]
    public void PlanetAndComponentKeepBoundedTimeWithoutObservingDrift(int opcode, int length)
    {
        foreach (var (correction, timestamp, expected) in new[]
        {
            (0u, 0x7fffffffu, 0x7fffffffu),
            (0u, 0x80000000u, 0x7fffffffu),
            (0u, 0xffffffffu, 0x7fffffffu),
            (0xffffffffu, 0u, 0x80000000u),
        })
        {
            var world = new Net7WorldState();
            var obj = world.MaterializeCreate(new(123, 1, 1, 4, 0, 0, 0));
            byte[] sync = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(sync.AsSpan(4), correction);
            BinaryPrimitives.WriteUInt32LittleEndian(sync.AsSpan(8), correction);
            world.Time.ApplySetClientTime(sync, 0);
            byte[] packet = new byte[length];
            BinaryPrimitives.WriteUInt32LittleEndian(packet, 123);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), timestamp);
            WriteSingle(packet, 8, 42);
            world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode((ushort)opcode, packet), 100);
            Assert.Equal(expected, obj.Spatial!.ClientTimestamp);
            Assert.Equal(42f, obj.Spatial.Position.X);
            Assert.Equal(0, world.Time.DriftSampleCount);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), correction);
            WriteSingle(packet, 8, 43);
            world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode((ushort)opcode, packet), 101);
            Assert.Equal(0u, obj.Spatial!.ClientTimestamp);
            Assert.Equal(43f, obj.Spatial.Position.X);
            Assert.Equal(0, world.Time.DriftSampleCount);
        }
    }

    [Fact]
    public void NonFiniteSimpleUpdateKeepsSpatialStateAndClockThenAcceptsNextUpdate()
    {
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        for (int offset = 8; offset < 48; offset += 4)
        {
            var world = new Net7WorldState();
            var obj = world.MaterializeCreate(new(123, 1, 1, 4, 0, 0, 0));
            byte[] packet = new byte[48];
            BinaryPrimitives.WriteUInt32LittleEndian(packet, 123);
            WriteSingle(packet, 32, 1);
            world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0008, packet), 100);
            var previous = obj.Spatial;
            int samples = world.Time.DriftSampleCount;
            byte[] invalid = (byte[])packet.Clone();
            WriteSingle(invalid, offset, value);
            world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0008, invalid), 200);
            Assert.Same(previous, obj.Spatial);
            Assert.Equal(samples, world.Time.DriftSampleCount);
            WriteSingle(packet, 8, 42);
            world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0008, packet), 300);
            Assert.Equal(42f, obj.Spatial!.Position.X);
            Assert.Equal(samples + 1, world.Time.DriftSampleCount);
        }
    }

    [Fact]
    public void PartialFormationReplacesPriorOffsetUsingInitializedBytes()
    {
        Assert.Equal(new Net7FormationPositionUpdate(0, 0, Vector3.Zero),
            Net7WorldWire.DecodeFormationPosition([]));
        var world = new Net7WorldState();
        var obj = world.MaterializeCreate(new(123, 1, 1, 4, 0, 0, 0));
        byte[] full = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(full, 123);
        BinaryPrimitives.WriteUInt32LittleEndian(full.AsSpan(4), 456);
        WriteSingle(full, 8, 50);
        WriteSingle(full, 12, 60);
        WriteSingle(full, 16, 70);
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0041, full), 100);
        Assert.Equal(new Vector3(50, 60, 70), obj.Spatial!.Formation!.Offset);
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0041, full[..14]), 200);
        Assert.Equal(new Vector3(50, 0, 0), obj.Spatial!.Formation!.Offset);
        Assert.Equal(456u, obj.Spatial.Formation.LeaderId);
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0041, full[..5]), 300);
        Assert.Equal(200u, obj.Spatial!.Formation!.LeaderId);
        Assert.Equal(Vector3.Zero, obj.Spatial.Formation.Offset);
    }

    [Fact]
    public void PartialRelationshipAppliesDefaultsInsteadOfKeepingPriorState()
    {
        Assert.Equal(new Net7RelationshipUpdate(uint.MaxValue, 1, 0),
            Net7WorldWire.DecodeRelationship([]));
        Assert.Equal(new Net7RelationshipUpdate(0x12ffffff, 1, 0),
            Net7WorldWire.DecodeRelationship([0x12]));
        var world = new Net7WorldState();
        var obj = world.MaterializeCreate(new(123, 1, 1, 0, 0, 0, 0));
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0089,
            new byte[] { 0, 0, 0, 123, 3, 0, 0, 0, 1 }), 100);
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0089,
            new byte[] { 0, 0, 0, 123 }), 200);
        Assert.Equal(1u, obj.RelationshipRaw);
        Assert.Equal((byte)0, obj.IsAttacking);
        Assert.Equal(2UL, obj.RelationshipNotificationRevision);
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0089,
            new byte[] { 0, 0, 0, 123, 0x34, 0x12 }), 300);
        Assert.Equal(0x1234u, obj.RelationshipRaw);
        Assert.Equal(3UL, obj.RelationshipNotificationRevision);
    }

    [Theory]
    [InlineData(0x008A)]
    [InlineData(0x008B)]
    public void ShortRelationshipBatchesApplyEveryRetainedEntry(int opcode)
    {
        // A partial id read retains prior bytes; even an empty read swaps them.
        byte[] payload = opcode == 0x008A
            ? [0, 0, 0, 3, 0x12, 0x34, 0x56, 0x78, 3, 0, 0, 0, 0xab, 0xcd]
            : [1, 0, 0, 0, 3, 0x12, 0x34, 0x56, 0x78, 0xab, 0xcd];
        var world = new Net7WorldState();
        uint[] ids = [0x12345678, 0xabcd3412, 0x1234cdab];
        foreach (uint id in ids) world.MaterializeCreate(new(id, 1, 1, 0, 0, 0, 0));
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode((ushort)opcode, payload), 100);
        foreach (uint id in ids)
        {
            var obj = world.Objects.FindFirst(id)!;
            Assert.Equal(1UL, obj.RelationshipNotificationRevision);
            if (opcode == 0x008A) Assert.Equal(3u, obj.RelationshipRaw);
            else Assert.Equal((byte)1, obj.IsAttacking);
        }
    }

    [Fact]
    public void ReactionBatchKeepsDefaultsAndPartiallyReadReaction()
    {
        Assert.Empty(Net7WorldWire.DecodeReactionBatch([]).Entries);
        Assert.Empty(Net7WorldWire.DecodeAttackerBatch([]).ObjectIds);
        var defaults = Net7WorldWire.DecodeReactionBatch([0, 0, 0, 2]);
        Assert.All(defaults.Entries, e => Assert.Equal(new Net7ReactionEntry(0, 1), e));
        var partial = Net7WorldWire.DecodeReactionBatch([0, 0, 0, 1, 0, 0, 0, 7, 0x34, 0x12]);
        Assert.Equal(new Net7ReactionEntry(7, 0x1234), Assert.Single(partial.Entries));
        Assert.Equal(2, defaults.Entries.Count);
        Assert.Equal(0u, defaults.TrailingRepeats);

        // An all-0xFF count (4,294,967,295 iterations in 00873F70) keeps cycling the
        // swapped/unswapped retained id after the payload ends; it must decode
        // without count-many entries.
        var unbounded = Net7WorldWire.DecodeReactionBatch([0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 9, 5, 0, 0, 0]);
        Assert.Equal([new Net7ReactionEntry(9, 5), new Net7ReactionEntry(0x09000000, 5), new Net7ReactionEntry(9, 5)], unbounded.Entries);
        Assert.Equal(uint.MaxValue - 3, unbounded.TrailingRepeats);
        var attackers = Net7WorldWire.DecodeAttackerBatch([1, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 9]);
        Assert.Equal([9u, 0x09000000u, 9u], attackers.ObjectIds);
        Assert.Equal(uint.MaxValue - 3, attackers.TrailingRepeats);

        // Applying the compact tail matches applying every iteration one by one.
        var world = new Net7WorldState();
        world.MaterializeCreate(new(9, 1, 1, 0, 0, 0, 0));
        world.MaterializeCreate(new(0x09000000, 1, 1, 0, 0, 0, 0));
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x008A, new byte[] { 0, 0, 0, 7, 0, 0, 0, 9, 5, 0, 0, 0 }), 100);
        Assert.Equal(4UL, world.Objects.FindFirst(9)!.RelationshipNotificationRevision);
        Assert.Equal(3UL, world.Objects.FindFirst(0x09000000)!.RelationshipNotificationRevision);
        Assert.Equal(5u, world.Objects.FindFirst(0x09000000)!.RelationshipRaw);
    }

    [Theory]
    [InlineData(0x003F, 48, 8)]
    [InlineData(0x0040, 32, 4)]
    [InlineData(0x0041, 20, 8)]
    [InlineData(0x0046, 64, 8)]
    public void UngatedSpatialUpdatesRetainNonFinitePosition(int opcode, int size, int offset)
    {
        // 00734A40/00733C80/00734740/00734BF0 admit these updates; their providers
        // copy the position rather than imposing the simple-movement gate.
        byte[] payload = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 123);
        WriteSingle(payload, offset, float.NaN);
        var world = new Net7WorldState();
        var obj = world.MaterializeCreate(new(123, 1, 1, 4, 0, 0, 0));
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode((ushort)opcode, payload), 100);
        Assert.NotNull(obj.Spatial);
        Assert.True(float.IsNaN(obj.Spatial.Position.X));
    }

    [Theory]
    [InlineData(0x0089)]
    [InlineData(0x008A)]
    [InlineData(0x008B)]
    public void RelationshipPacketsWithUnusedTailStillUpdateTheObject(int opcode)
    {
        byte[] payload;
        if (opcode == 0x0089)
        {
            payload = new byte[9];
            BinaryPrimitives.WriteUInt32BigEndian(payload, 123);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 3);
            payload[8] = 1;
        }
        else if (opcode == 0x008A)
        {
            payload = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(payload, 1);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), 123);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 3);
        }
        else
        {
            payload = new byte[9];
            payload[0] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1), 1);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(5), 123);
        }
        var world = new Net7WorldState();
        var obj = world.MaterializeCreate(new(123, 1, 1, 0, 0, 0, 0));
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode((ushort)opcode,
            (byte[])[.. payload, 0xA5, 0xFF]), 100);
        Assert.Equal(1UL, obj.RelationshipNotificationRevision);
        if (opcode != 0x008B) Assert.Equal(3u, obj.RelationshipRaw);
        if (opcode != 0x008A) Assert.Equal((byte)1, obj.IsAttacking);
    }

    [Fact]
    public void NavigationRetainsInitializedBytesOnShortReadAndIgnoresUnusedTail()
    {
        var empty = Net7WorldWire.DecodeNavigation([]);
        Assert.Equal(new Net7NavigationUpdate(0, 0, 0, uint.MaxValue, 0), empty);
        byte[] packet = new byte[14];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, 123);
        WriteSingle(packet, 4, 50);
        packet[8] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(9), 7);
        packet[13] = 1;
        Assert.Equal(new Net7NavigationUpdate(123, 50, 1, 7, 1), Net7WorldWire.DecodeNavigation(packet));
        Assert.Equal(Net7WorldWire.DecodeNavigation(packet), Net7WorldWire.DecodeNavigation([.. packet, 0xff, 0x25]));
        Assert.Equal(0xffffff07u, Net7WorldWire.DecodeNavigation(packet.AsSpan(0, 10)).NavType);
        Assert.Equal(0u, Net7WorldWire.DecodeNavigationDelete([]));
        Assert.Equal(0x1234u, Net7WorldWire.DecodeNavigationDelete([0x34, 0x12]));
        Assert.Equal(123u, Net7WorldWire.DecodeNavigationDelete([123, 0, 0, 0, 0xff]));
    }

    [Fact]
    public void CreateAndRelationshipUseTheirProvenMixedEndianLayouts()
    {
        byte[] create = new byte[23];
        BinaryPrimitives.WriteUInt32LittleEndian(create.AsSpan(0, 4), 0x11223344);
        WriteSingle(create, 4, 1.5f);
        BinaryPrimitives.WriteInt16LittleEndian(create.AsSpan(8, 2), -123);
        create[10] = 0x25;
        WriteSingle(create, 11, 0.1f);
        WriteSingle(create, 15, 0.2f);
        WriteSingle(create, 19, 0.3f);

        Net7CreateUpdate c = Net7WorldWire.DecodeCreate(create);
        Assert.Equal(0x11223344u, c.GameId);
        Assert.Equal(-123, c.BaseAsset);
        Assert.Equal((byte)0x25, c.ObjectType);

        byte[] relation = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(relation.AsSpan(0, 4), 0xA1B2C3D4);
        BinaryPrimitives.WriteUInt32LittleEndian(relation.AsSpan(4, 4), 3);
        relation[8] = 1;
        Net7RelationshipUpdate r = Net7WorldWire.DecodeRelationship(relation);
        Assert.Equal(0xA1B2C3D4u, r.ObjectId);
        Assert.Equal(Net7Reaction.Adoration, r.DefinedReaction);
        Assert.Equal((byte)1, r.IsAttacking);
    }

    [Fact]
    public void AdvancedPositionAppliesAllAbsentFieldDefaults()
    {
        byte[] payload = new byte[42];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6, 4), 2);
        WriteQuaternion(payload, 22, Quaternion.Identity);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(38, 4), 3);

        Net7AdvancedPositionUpdate update = Net7WorldWire.DecodeAdvancedPosition(payload);
        Assert.Equal(0f, update.CurrentSpeed);
        Assert.Equal(0f, update.SetSpeed);
        Assert.Equal(-MathF.PI, update.DesiredY);
        Assert.Equal(-1000f, update.DesiredZ);
        Assert.Equal(500u, update.UpdatePeriod);
    }


    [Fact]
    public void AdvancedPositionAllOptionGroupsUseExactSequentialOffsets()
    {
        byte[] payload = new byte[98];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), 0x01FF);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2, 4), 0x01020304);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6, 4), 0x11223344);
        WriteVector3(payload, 10, new Vector3(1, 2, 3));
        WriteQuaternion(payload, 22, Quaternion.Identity);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(38, 4), 0x55667788);
        WriteSingle(payload, 42, 4f);
        WriteSingle(payload, 46, 5f);
        WriteSingle(payload, 50, 6f);
        WriteSingle(payload, 54, 7f);
        WriteSingle(payload, 58, 8f);
        WriteSingle(payload, 62, 9f);
        WriteSingle(payload, 66, 10f);
        WriteVector3(payload, 70, new Vector3(11, 12, 13));
        WriteSingle(payload, 82, 14f);
        WriteSingle(payload, 86, 15f);
        WriteSingle(payload, 90, 16f);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(94, 4), 777);

        Net7AdvancedPositionUpdate update = Net7WorldWire.DecodeAdvancedPosition(payload);
        Assert.Equal(0x01020304u, update.GameId);
        Assert.Equal(0x11223344u, update.ServerTimestamp);
        Assert.Equal(0x55667788u, update.MovementId);
        Assert.Equal(4f, update.CurrentSpeed);
        Assert.Equal(5f, update.SetSpeed);
        Assert.Equal(6f, update.Acceleration);
        Assert.Equal(7f, update.RotY);
        Assert.Equal(8f, update.DesiredY);
        Assert.Equal(9f, update.RotZ);
        Assert.Equal(10f, update.DesiredZ);
        Assert.Equal(new Vector3(11, 12, 13), update.ImpartedVelocity);
        Assert.Equal(14f, update.ImpartedSpin);
        Assert.Equal(15f, update.ImpartedRoll);
        Assert.Equal(16f, update.ImpartedPitch);
        Assert.Equal(777u, update.UpdatePeriod);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void CreateAndAdvancedObjectsRetainNonFiniteFieldsAndFollowingUpdates(float value)
    {
        var world = new Net7WorldState();
        byte[] create = new byte[23];
        BinaryPrimitives.WriteUInt32LittleEndian(create, 101);
        foreach (int offset in new[] { 4, 11, 15, 19 }) WriteSingle(create, offset, value);
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0004, create), 100);
        var created = world.Objects.FindFirst(101)!;
        Assert.Equal(value, created.Scale);
        Assert.Equal(value, created.HsvH);
        Assert.Equal(value, created.HsvS);
        Assert.Equal(value, created.HsvV);

        byte[] a = new byte[61];
        BinaryPrimitives.WriteUInt32LittleEndian(a, 102);
        a[4] = 0x25;
        a[23] = 2;
        a[57] = 0x10;
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(58), 1);
        a[60] = (byte)'A';
        foreach (int offset in new[] { 7, 11, 15, 19, 25, 29, 33, 37, 41, 45, 49, 53 })
            WriteSingle(a, offset, value);
        var first = world.ApplyAdvancedObjectA(Net7WorldWire.DecodeAdvancedObjectA(a));
        Assert.Equal(value, first.Scale);
        Assert.Equal(value, first.HsvH);
        Assert.Equal("A", first.Name);
        Assert.Equal(2u, first.RelationshipRaw);
        Assert.NotNull(first.Navigation);
        Assert.Equal(value, first.Spatial!.Position.X);
        Assert.Equal(value, first.Spatial.Orientation.W);

        byte[] b = new byte[49];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 103);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(46), 1);
        b[48] = (byte)'B';
        foreach (int offset in new[] { 6, 10, 14, 18, 22, 26, 30, 34, 38, 42 })
            WriteSingle(b, offset, value);
        var second = world.ApplyAdvancedObjectB(Net7WorldWire.DecodeAdvancedObjectB(b));
        Assert.Equal(value, second.Scale);
        Assert.Equal(value, second.HsvH);
        Assert.Equal("B", second.Name);
        Assert.Equal(2u, second.RelationshipRaw);

        // A later ordinary update still reaches the existing object.
        byte[] position = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(position, 102);
        WriteVector3(position, 4, new Vector3(1, 2, 3));
        WriteQuaternion(position, 16, Quaternion.Identity);
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0040, position), 200);
        Assert.Same(first, world.Objects.FindFirst(102));
        Assert.Equal("A", first.Name);
        Assert.Equal(new Vector3(1, 2, 3), first.Spatial!.Position);
        Assert.Equal(Quaternion.Identity, first.Spatial.Orientation);
    }

    [Fact]
    public void AdvancedObjectBConsumesItsFortyEightBytePrefixAndPreservesCStringPolicy()
    {
        byte[] payload = new byte[48 + 4 + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), 0xAABBCCDD);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), 88);
        WriteSingle(payload, 6, 1.25f);
        WriteSingle(payload, 10, 0.4f);
        WriteSingle(payload, 14, 0.5f);
        WriteVector3(payload, 18, new Vector3(6, 7, 8));
        WriteQuaternion(payload, 30, Quaternion.Identity);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(46, 2), 4);
        Encoding.Latin1.GetBytes("B\0CD").CopyTo(payload, 48);
        payload[^2] = 0xEE;
        payload[^1] = 0xFF;

        Net7AdvancedObjectB update = Net7WorldWire.DecodeAdvancedObjectB(payload);
        Assert.Equal(0xAABBCCDDu, update.GameId);
        Assert.Equal((ushort)88, update.BaseAsset);
        Assert.Equal(new Vector3(6, 7, 8), update.Position);
        Assert.Equal("B", update.EffectiveName);
    }

    [Fact]
    public void PlanetAndFormationLayoutsAreExact()
    {
        byte[] planet = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(planet.AsSpan(0, 4), 10);
        BinaryPrimitives.WriteUInt32LittleEndian(planet.AsSpan(4, 4), 20);
        WriteVector3(planet, 8, new Vector3(1, 2, 3));
        BinaryPrimitives.WriteUInt32LittleEndian(planet.AsSpan(20, 4), 30);
        for (int i = 0; i < 6; i++)
            WriteSingle(planet, 24 + i * 4, i + 0.5f);
        Net7PlanetPositionUpdate p = Net7WorldWire.DecodePlanetPosition(planet);
        Assert.Equal(10u, p.GameId);
        Assert.Equal(new Vector3(1, 2, 3), p.SpatialVector);
        Assert.Equal(30u, p.OrbitId);
        Assert.Equal(5.5f, p.TiltAngle);

        byte[] formation = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(formation.AsSpan(0, 4), 11);
        BinaryPrimitives.WriteUInt32LittleEndian(formation.AsSpan(4, 4), 12);
        WriteVector3(formation, 8, new Vector3(4, 5, 6));
        Net7FormationPositionUpdate f = Net7WorldWire.DecodeFormationPosition(formation);
        Assert.Equal(11u, f.GameId);
        Assert.Equal(12u, f.LeaderId);
        Assert.Equal(new Vector3(4, 5, 6), f.Offset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HandoffPreservesExactSectorJoinImageAndFourSharedStrings(bool unusedTail)
    {
        byte[] join = Enumerable.Range(0, Net7SectorJoinClientData.WireSize).Select(i => (byte)i).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(join.AsSpan(0x10, 4), 0x11223344);
        BinaryPrimitives.WriteUInt32BigEndian(join.AsSpan(0x14, 4), 1079);
        BinaryPrimitives.WriteUInt32LittleEndian(join.AsSpan(0x1C, 4), 2);
        using var stream = new MemoryStream();
        stream.Write(join);
        WriteShared(stream, "SourceSector");
        WriteShared(stream, "SourceSystem");
        WriteShared(stream, "DestSector");
        WriteShared(stream, "DestSystem");

        if (unusedTail) stream.Write([0xff, 0xa5, 0x19]);
        var world = new Net7WorldState();
        world.MaterializeCreate(new(123, 1, 1, 0, 0, 0, 0));
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x003A, stream.ToArray()), 100);
        Assert.Null(world.Objects.FindFirst(123));
        Net7ServerHandoff handoff = Assert.IsType<Net7ServerHandoff>(world.PendingHandoff);
        Assert.Equal(join, handoff.NextJoinData.ToWireBytes());
        Assert.Equal("SourceSector", handoff.SourceSectorName);
        Assert.Equal("DestSystem", handoff.DestinationSystemName);
    }

    [Fact]
    public void AdvancedObjectAConsumesPrefixAndAllowsTranslatorIgnoredTrailingBytes()
    {
        byte[] payload = new byte[60 + 4 + 3];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), 0x01020304);
        payload[4] = 0x25;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(5, 2), 77);
        WriteSingle(payload, 7, 2f);
        WriteSingle(payload, 11, 0.1f);
        WriteSingle(payload, 15, 0.2f);
        WriteSingle(payload, 19, 0.3f);
        payload[23] = 2;
        payload[24] = 0x99;
        WriteVector3(payload, 25, new Vector3(1, 2, 3));
        WriteQuaternion(payload, 37, Quaternion.Identity);
        WriteSingle(payload, 53, 900f);
        payload[57] = 0xF5;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(58, 2), 4);
        Encoding.Latin1.GetBytes("A\0BC").CopyTo(payload, 60);
        payload[^3] = 0xAA;
        payload[^2] = 0xBB;
        payload[^1] = 0xCC;

        Net7AdvancedObjectA update = Net7WorldWire.DecodeAdvancedObjectA(payload);
        Assert.Equal("A", update.EffectiveName);
        Assert.Equal((byte)0xF5, update.Flags);
        Assert.Equal(900f, update.SharedNavigationRadarScalar);
    }

    [Fact]
    public void EmptyAuxViewAndUnusedMovementBitsAreAccepted()
    {
        byte[] aux = new byte[6];
        Assert.Empty(Net7WorldWire.DecodeAuxDataEnvelope(0x001B, aux).Delta);

        byte[] advanced = new byte[42];
        BinaryPrimitives.WriteUInt16LittleEndian(advanced.AsSpan(0, 2), 0x0200);
        var update = Net7WorldWire.DecodeAdvancedPosition(advanced);
        Assert.Equal(0x0200, update.OptionalMask);
        Assert.Equal(500u, update.UpdatePeriod);
        byte[] tailed = [.. advanced, 0xFF, 0x91];
        Assert.Equal(update, Net7WorldWire.DecodeAdvancedPosition(tailed));
    }

    [Fact]
    public void AdvancedShortReadsPreserveConstructorDefaultsByteForByte()
    {
        byte[] full = new byte[98];
        BinaryPrimitives.WriteUInt16LittleEndian(full, 0x01FF);
        for (int i = 2; i < full.Length; i++) full[i] = (byte)(i * 13);
        for (int length = 2; length <= full.Length; length++)
        {
            byte[] expected = new byte[98];
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(94), 500);
            full.AsSpan(0, length).CopyTo(expected);
            Assert.Equal(Net7WorldWire.DecodeAdvancedPosition(expected),
                Net7WorldWire.DecodeAdvancedPosition(full.AsSpan(0, length)));
        }
        Assert.Equal(500u, Net7WorldWire.DecodeAdvancedPosition([]).UpdatePeriod);
    }

    [Theory]
    [InlineData(10, false)] [InlineData(14, false)] [InlineData(18, false)]
    [InlineData(22, false)] [InlineData(26, false)] [InlineData(30, false)] [InlineData(34, false)]
    [InlineData(42, false)]
    [InlineData(46, true)] [InlineData(50, true)] [InlineData(54, true)]
    [InlineData(58, true)] [InlineData(62, true)] [InlineData(66, true)]
    [InlineData(70, false)] [InlineData(74, false)] [InlineData(78, false)]
    [InlineData(82, true)] [InlineData(86, true)] [InlineData(90, true)]
    public void AdvancedAdmissionChecksOnlyNativeGateFields(int offset, bool accepted)
    {
        byte[] payload = new byte[98];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 0x01FF);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), 123);
        WriteQuaternion(payload, 22, Quaternion.Identity);
        WriteSingle(payload, offset, float.NaN);
        var world = new Net7WorldState();
        var obj = world.MaterializeCreate(new(123, 1, 1, 0, 0, 0, 0));
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x003E, payload), 100);
        Assert.Equal(accepted, obj.Spatial is not null);
    }

    [Theory]
    [InlineData(0x0008, 48)]
    [InlineData(0x003F, 48)]
    [InlineData(0x0040, 32)]
    [InlineData(0x0041, 20)]
    [InlineData(0x0046, 64)]
    public void MovementUnusedTailDoesNotDiscardUpdate(int opcode, int size)
    {
        byte[] payload = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 123);
        var exact = new Net7WorldState();
        var extended = new Net7WorldState();
        var expected = exact.MaterializeCreate(new(123, 1, 1, 4, 0, 0, 0));
        var actual = extended.MaterializeCreate(new(123, 1, 1, 4, 0, 0, 0));
        exact.Apply(EnbNext.Protocol.Net.Packet.ForOpcode((ushort)opcode, payload), 100);
        extended.Apply(EnbNext.Protocol.Net.Packet.ForOpcode((ushort)opcode,
            (byte[])[.. payload, 0xA5, 0xFF, 0x37]), 100);
        Assert.NotNull(expected.Spatial);
        Assert.NotNull(actual.Spatial);
        Assert.Equal(expected.Spatial.Kind, actual.Spatial.Kind);
        Assert.Equal(expected.Spatial.Position, actual.Spatial.Position);
        Assert.Equal(expected.Spatial.ClientTimestamp, actual.Spatial.ClientTimestamp);
    }

    [Fact]
    public void CreateTargetAndRemoveIgnoreUnusedPacketBytes()
    {
        var world = new Net7WorldState();
        byte[] create = new byte[26];
        BinaryPrimitives.WriteUInt32LittleEndian(create, 123);
        WriteSingle(create, 4, 1);
        create[10] = 0x1C;
        create[23] = 0xFF;
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0004, create), 100);
        var obj = world.Objects.FindFirst(123);
        Assert.NotNull(obj);
        byte[] target = new byte[11];
        BinaryPrimitives.WriteUInt32LittleEndian(target, 456);
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(4), 123);
        target[8] = 0xFF;
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0019, target), 100);
        Assert.Equal(456u, obj.SpecialTargetEndpointId);
        byte[] remove = new byte[7];
        BinaryPrimitives.WriteUInt32LittleEndian(remove, 123);
        remove[4] = 0xFF;
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0007, remove), 100);
        Assert.Null(world.Objects.FindFirst(123));
    }

    private static void WriteSingle(byte[] bytes, int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));

    private static void WriteVector3(byte[] bytes, int offset, Vector3 value)
    {
        WriteSingle(bytes, offset, value.X);
        WriteSingle(bytes, offset + 4, value.Y);
        WriteSingle(bytes, offset + 8, value.Z);
    }

    private static void WriteQuaternion(byte[] bytes, int offset, Quaternion value)
    {
        WriteSingle(bytes, offset, value.X);
        WriteSingle(bytes, offset + 4, value.Y);
        WriteSingle(bytes, offset + 8, value.Z);
        WriteSingle(bytes, offset + 12, value.W);
    }

    private static void WriteShared(Stream stream, string value)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)bytes.Length));
        stream.Write(length);
        stream.Write(bytes);
    }
}
