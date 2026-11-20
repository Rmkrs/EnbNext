using EnbNext.Engine;
using System.Buffers.Binary;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7TurretAuxDataTests
{
    // Inbound decoded 0x001B from the normal Jupiter entry capture
    // sector-entry capture 20260911T124823834Z, UDP sequence 36.
    private const string Gate = "A08701003700011630004000000C800300BE40F8C00714004761746520477561726469616E205475727265748096184B8096184B4200000005050505050100000000000000";
    private const string Starbase = "9F8701003B00011630004000000C800300BE40F8C0071800537461726261736520477561726469616E205475727265748096184B8096184B4200000005050505050100000000000000";

    [Theory]
    [InlineData("148901003800011630004000000C800300BE40F8C00715005368696E776120506174726F6C204372756973657200802245008022451900000005050505050100000000000000", "14890100130001020000000000040001000000", 9, 1)]
    [InlineData("3A8901003700011630004000000C800300BE40F8C00714004761746520477561726469616E205475727265748096184B8096184B4200000005050505050100000000000000", "3A890100130001020000000000040000000000", 14, 0)]
    public void SameDatagramFollowupSuppliesThrustWithoutApplyingIgnoredTrail(string initialHex,string followupHex,short baseAsset,int thrust)
    {
        byte[] initial=Convert.FromHexString(initialHex);
        var world=new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(BinaryPrimitives.ReadUInt32LittleEndian(initial),0,baseAsset,0,0,0,0));
        var initialResult=world.Apply(Packet.ForOpcode(0x001B,initial),0);
        Assert.Null(initialResult.AuxDataWarning);
        Assert.NotNull(initialResult.AuxDataCompatibilityNote);
        var issues=new EnbNext.Engine.LiveIssueLog();
        issues.ObserveDiagnostic($"WORLD-09 0x001B compatibility: {initialResult.AuxDataCompatibilityNote}");
        Assert.Equal(0,issues.Count);
        Assert.Null(world.Apply(Packet.ForOpcode(0x001B,Convert.FromHexString(followupHex)),0).AuxDataWarning);
        var obj=Assert.Single(world.Objects.Snapshot());
        Assert.Equal(thrust,obj.AuxData!.Resolve(1921).Value);
        Assert.Null(obj.AuxData.Resolve(1922).Value);
        Assert.Equal(2,obj.AuxDataAudit!.AttemptCount);
        Assert.False(obj.AuxDataAudit.LargestAccepted);
        Assert.True(obj.AuxDataAudit.LargestRetailCompatiblePartial);
    }

    [Theory]
    [InlineData("148901003800011630004000000C800300BE40F8C00715005368696E776120506174726F6C204372756973657200802245008022451900000005050505050100000000000000", 9)]
    [InlineData("3A8901003700011630004000000C800300BE40F8C00714004761746520477561726469616E205475727265748096184B8096184B4200000005050505050100000000000000", 14)]
    public void OfflineProducerLengthCorrectionAloneMakesCapturedDeltaComplete(string hex,short baseAsset)
    {
        byte[] original=Convert.FromHexString(hex);
        byte[] corrected=original.ToArray();
        Assert.Equal(8,corrected.Length-6-BinaryPrimitives.ReadUInt16LittleEndian(corrected.AsSpan(4)));
        // Experimental producer correction, ONLY in this offline fixture. Never
        // expand an incoming view or synthesize a packet on the live transport.
        BinaryPrimitives.WriteUInt16LittleEndian(corrected.AsSpan(4),checked((ushort)(corrected.Length-6)));
        Assert.Equal(original[..4],corrected[..4]);
        Assert.Equal(original[6..],corrected[6..]);
        var world=new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(BinaryPrimitives.ReadUInt32LittleEndian(corrected),0,baseAsset,0,0,0,0));
        Assert.Null(world.Apply(Packet.ForOpcode(0x001B,corrected),0).AuxDataWarning);
        var obj=Assert.Single(world.Objects.Snapshot());
        Assert.True(obj.AuxDataAudit!.LargestAccepted);
        Assert.Equal(1,obj.AuxData!.Resolve(1921).Value);
        Assert.Equal(0,obj.AuxData.Resolve(1922).Value);
    }

    [Theory]
    [InlineData("148901003800011630004000000C800300BE40F8C00715005368696E776120506174726F6C204372756973657200802245008022451900000005050505050100000000000000", "Shinwa Patrol Cruiser", 9, 25, 2600f)]
    [InlineData("3A8901003700011630004000000C800300BE40F8C00714004761746520477561726469616E205475727265748096184B8096184B4200000005050505050100000000000000", "Gate Guardian Turret", 14, 66, 10000000f)]
    public void SwoopingEagleSeptember13PreservesCapturedPrefix(string hex, string name, short baseAsset, int level, float hull)
    {
        // Passive 20260913T055147250Z capture, both reports at 07:51:53.
        byte[] payload = Convert.FromHexString(hex);
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(BinaryPrimitives.ReadUInt32LittleEndian(payload), 0, baseAsset, 0, 0, 0, 0));
        var result = world.Apply(Packet.ForOpcode(0x001B, payload), 0);
        Assert.True(result.PacketMaterialized);
        Assert.Null(result.AuxDataWarning);
        Assert.NotNull(result.AuxDataCompatibilityNote);
        var obj = Assert.Single(world.Objects.Snapshot());
        Assert.Equal(name, obj.Name);
        Assert.Equal(level, obj.AuxData!.Resolve(33).Value);
        Assert.Null(obj.AuxData.Resolve(1921).Value);
        Assert.Null(obj.AuxData.Resolve(1922).Value);
        var shown = Assert.Single(new EnbPresentationSnapshotBuilder().Build(world, null, 0).Objects);
        Assert.Equal(hull, shown.TargetVitals!.HullPoints);
        Assert.Equal(hull, shown.TargetVitals.MaximumHullPoints);
    }

    [Theory]
    [InlineData(Gate, "Gate Guardian Turret")]
    [InlineData(Starbase, "Starbase Guardian Turret")]
    public void CapturedShortDeclaredDeltaPublishesCompletedNameAndHull(string hex, string name)
    {
        byte[] payload = Convert.FromHexString(hex);
        var world = CreateWorld(payload);
        var result = world.Apply(Packet.ForOpcode(0x001B, payload), 0);

        Assert.True(result.PacketMaterialized);
        Assert.Null(result.AuxDataWarning);
        Assert.NotNull(result.AuxDataCompatibilityNote);
        var obj = Assert.Single(world.Objects.Snapshot());
        Assert.Equal(name, obj.Name);
        Assert.Equal(66, obj.AuxData!.Resolve(33).Value);
        Assert.False(obj.AuxDataAudit!.LargestAccepted);
        Assert.True(obj.AuxDataAudit.LargestRetailCompatiblePartial);
        Assert.Contains("1921",obj.AuxDataAudit.LargestError);
        // Match retail: the extra bytes explain the partial update, but cannot
        // change engine values or states outside the declared view.
        Assert.Null(obj.AuxData.Resolve(1921).Value);
        Assert.Null(obj.AuxData.Resolve(1922).Value);
        Assert.Equal(0,obj.AuxData.Resolve(1921).State);

        var snapshot = Assert.Single(new EnbPresentationSnapshotBuilder().Build(world, null, 0).Objects);
        Assert.True(snapshot.ShipAuxDataAudit!.LargestRetailCompatiblePartial);
        Assert.Equal(name, snapshot.Name);
        Assert.Equal(10_000_000f, snapshot.TargetVitals!.HullPoints);
        Assert.Equal(10_000_000f, snapshot.TargetVitals.MaximumHullPoints);

        // The later captured shield delta must not erase the retained identity/hull.
        if (name == "Gate Guardian Turret")
        {
            world.Apply(Packet.ForOpcode(0x001B, Convert.FromHexString(
                "A0870100270001060C00000000002000000000000078034F1A000017B7D1380000803F8096184B04004E6F6E65")), 0);
            var later = Assert.Single(new EnbPresentationSnapshotBuilder().Build(world, null, 0).Objects);
            Assert.Equal(name, later.Name);
            Assert.Equal(10_000_000f, later.TargetVitals!.HullPoints);
            Assert.Equal(10_000_000f, later.TargetVitals.MaximumShieldPower);
            Assert.Equal(2, obj.AuxDataAudit.AttemptCount);
            Assert.False(obj.AuxDataAudit.LargestAccepted);
        }
    }

    [Theory]
    [InlineData(40)] // Within HullPoints, after the complete name.
    [InlineData(54)] // Missing the last nested aggregate record.
    public void MidValueAndNestedRecordTruncationRetainsCompletedFields(int length)
    {
        byte[] payload = Convert.FromHexString(Gate);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), (ushort)length);
        var world = CreateWorld(payload);
        var result = world.Apply(Packet.ForOpcode(0x001B, payload), 0);
        Assert.Null(result.AuxDataWarning);
        Assert.NotNull(result.AuxDataCompatibilityNote);
        var obj = Assert.Single(world.Objects.Snapshot());
        Assert.Equal("Gate Guardian Turret", obj.Name);
        Assert.NotNull(obj.AuxData!.Resolve(1).Value);
        if (length == 40) Assert.Null(obj.AuxData.Resolve(15).Value);
        // A later full update still completes the fields that were unavailable.
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), 55);
        world.Apply(Packet.ForOpcode(0x001B, payload), 1);
        Assert.NotNull(obj.AuxData.Resolve(15).Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(12)]
    public void KnownIgnoredTailClassificationRequiresExactlyEightSuppliedBytes(int trailingLength)
    {
        byte[] captured=Convert.FromHexString(Gate);
        int declared=BinaryPrimitives.ReadUInt16LittleEndian(captured.AsSpan(4));
        byte[] payload=new byte[6+declared+trailingLength];
        captured.AsSpan(0,Math.Min(captured.Length,payload.Length)).CopyTo(payload);
        var world=CreateWorld(payload);
        var result=world.Apply(Packet.ForOpcode(0x001B,payload),0);
        Assert.Null(result.AuxDataWarning);
        Assert.Null(result.AuxDataCompatibilityNote);
        var issues=new EnbNext.Engine.LiveIssueLog();
        if (result.AuxDataWarning is not null)
            issues.ObserveDiagnostic($"WORLD-09 0x001B partial materialization: {result.AuxDataWarning}");
        Assert.Equal(0,issues.Count);
        var obj=Assert.Single(world.Objects.Snapshot());
        Assert.Equal("Gate Guardian Turret",obj.Name);
        Assert.Null(obj.AuxData!.Resolve(1921).Value);
        Assert.Null(obj.AuxData.Resolve(1922).Value);
    }

    [Fact]
    public void EightBytesForDifferentPropertiesKeepPrefixWithoutAnIssue()
    {
        var schema=new Net7AuxDataSchemaCatalog().CreateObjectSchema(0)!;
        schema.Resolve(1).Value=new Net7AuxDataString(System.Text.Encoding.Latin1.GetBytes("test ship"));
        schema.Resolve(1921).Value=7;
        schema.Resolve(1924).Value=new byte[]{1,2,3,4}; // GuildRank, not EngineTrailType.
        var codec=new Net7AuxDataCodec();
        byte[] delta=codec.Encode(schema,new[]{1,1921,1924}.Select(id=>new Net7AuxDataOperation(id,Net7AuxDataOperationKind.ValueUpdate)));
        Assert.Equal(1,delta[0]);
        byte[] payload=new byte[6+delta.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload,42);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4),checked((ushort)(delta.Length-8)));
        delta.CopyTo(payload,6);
        var world=CreateWorld(payload);
        var result=world.Apply(Packet.ForOpcode(0x001B,payload),0);
        Assert.Null(result.AuxDataWarning);
        Assert.Null(result.AuxDataCompatibilityNote);
        var actual=Assert.Single(world.Objects.Snapshot()).AuxData!;
        Assert.Equal("test ship",((Net7AuxDataString)actual.Resolve(1).Value!).Text);
        // Inspection must roll back its first (otherwise valid) engine int.
        Assert.Null(actual.Resolve(1921).Value);
        Assert.Null(actual.Resolve(1924).Value);
    }

    [Fact]
    public void TailInspectionDoesNotApplyEvenNonzeroSuppliedEngineValues()
    {
        byte[] payload=Convert.FromHexString(Gate);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(payload.Length-8),7);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(payload.Length-4),9);
        var world=CreateWorld(payload);
        Assert.NotNull(world.Apply(Packet.ForOpcode(0x001B,payload),0).AuxDataCompatibilityNote);
        var aux=Assert.Single(world.Objects.Snapshot()).AuxData!;
        Assert.Null(aux.Resolve(1921).Value);
        Assert.Null(aux.Resolve(1922).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadSuffixPreservesPreviouslyMaterializedEngineValues(bool physicallyPresent)
    {
        byte[] payload = Convert.FromHexString(Gate);
        // A genuinely absent suffix cannot be repaired.
        if (!physicallyPresent) payload = payload[..(6+BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4)))];
        var world = CreateWorld(payload);
        byte[] enginePacket = new byte[23];
        payload.AsSpan(0, 4).CopyTo(enginePacket);
        BinaryPrimitives.WriteUInt16LittleEndian(enginePacket.AsSpan(4), 17);
        enginePacket[6] = 1;
        enginePacket[7] = 2;
        enginePacket[13] = 0x0C; // Root value-map children 46 and 47.
        BinaryPrimitives.WriteInt32LittleEndian(enginePacket.AsSpan(15), 7);
        BinaryPrimitives.WriteInt32LittleEndian(enginePacket.AsSpan(19), 9);
        world.Apply(Packet.ForOpcode(0x001B, enginePacket), 0);
        world.Apply(Packet.ForOpcode(0x001B, payload), 0);
        var aux = Assert.Single(world.Objects.Snapshot()).AuxData!;
        Assert.Equal(7, aux.Resolve(1921).Value);
        Assert.Equal(9, aux.Resolve(1922).Value);
    }

    [Fact]
    public void TransactionalCodecStillRejectsAndRollsBackTheSameShortDelta()
    {
        byte[] payload = Convert.FromHexString(Gate);
        var obj = Assert.Single(CreateWorld(payload).Objects.Snapshot());
        var envelope = Net7WorldWire.DecodeAuxDataEnvelope(0x001B, payload);
        Assert.Equal(55, envelope.Delta.Length);
        Assert.Throws<InvalidDataException>(() => new Net7AuxDataCodec().Apply(obj.AuxData!, envelope.Delta));
        Assert.Null(obj.AuxData!.Resolve(1).Value);
        Assert.Null(obj.AuxData.Resolve(15).Value);
    }

    [Fact]
    public void NonFiniteRetainedShipFloatsStillPublishTheSceneAndFollowingUpdates()
    {
        // 004A5300 copies raw float bytes; a NaN/Infinity hull or shield must not
        // stall the whole scene snapshot (it previously threw from the builder).
        var schema=new Net7AuxDataSchemaCatalog().CreateObjectSchema(0)!;
        schema.Resolve(15).Value=float.NaN;
        schema.Resolve(16).Value=float.PositiveInfinity;
        var codec=new Net7AuxDataCodec();
        byte[] delta=codec.Encode(schema,new[]{15,16}.Select(id=>new Net7AuxDataOperation(id,Net7AuxDataOperationKind.ValueUpdate)));
        byte[] payload=new byte[6+delta.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload,43);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4),checked((ushort)delta.Length));
        delta.CopyTo(payload,6);
        var world=CreateWorld(payload);
        world.Apply(Packet.ForOpcode(0x001B,payload),0);
        var shown=Assert.Single(new EnbPresentationSnapshotBuilder().Build(world,null,0).Objects);
        Assert.True(float.IsNaN(shown.TargetVitals!.HullPoints!.Value));
        Assert.Equal(float.PositiveInfinity,shown.TargetVitals.MaximumHullPoints);

        schema.Resolve(15).Value=125f;
        delta=codec.Encode(schema,new[]{15}.Select(id=>new Net7AuxDataOperation(id,Net7AuxDataOperationKind.ValueUpdate)));
        payload=new byte[6+delta.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload,43);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4),checked((ushort)delta.Length));
        delta.CopyTo(payload,6);
        world.Apply(Packet.ForOpcode(0x001B,payload),1);
        Assert.Equal(125f,Assert.Single(new EnbPresentationSnapshotBuilder().Build(world,null,1).Objects).TargetVitals!.HullPoints);
    }

    [Fact]
    public void LightWithIndeterminateAttenuationFactorsKeepsItsSlotWithoutStallingTheScene()
    {
        // ConstructType8AuxData leaves AttenuationFactors uninitialized: a non-zero
        // Attenuation mode without materialized factors is undefined. Next presents
        // no light for it (it still occupies its slot) instead of failing the scene.
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(50, 1, 0, 0x08, 0, 0, 0));
        world.MaterializeCreate(new Net7CreateUpdate(51, 1, 0, 0x08, 0, 0, 0));
        var schema = new Net7AuxDataSchemaCatalog().CreateObjectSchema(0x08)!;
        schema.Resolve(6).Value = new byte[] { 1, 0, 0, 0 };
        byte[] delta = new Net7AuxDataCodec().Encode(schema, new[] { new Net7AuxDataOperation(6, Net7AuxDataOperationKind.ValueUpdate) });
        byte[] payload = new byte[6 + delta.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 50);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), checked((ushort)delta.Length));
        delta.CopyTo(payload, 6);
        world.Apply(Packet.ForOpcode(0x001B, payload), 0);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, world.Objects.FindFirst(50)!.AuxData!.Resolve(6).Value);

        var lighting = new EnbPresentationSnapshotBuilder().Build(world, null, 0).Lighting;
        Assert.Equal(51u, Assert.Single(lighting.Lights).GameId);
    }

    private static Net7WorldState CreateWorld(byte[] payload)
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(BinaryPrimitives.ReadUInt32LittleEndian(payload), 1, 14, 0, 0, 0, 0));
        return world;
    }
}
