using EnbNext.Net7.World;
using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbHarvestTests
{
    // Passive 2026-09-11 17:55Z selection of 0x18733. Full transport envelope
    // declares 354 delta bytes; the four bytes after that declared boundary
    // are not part of the delta passed to the retail visitor.
    private const string GasDelta = "01A602F6FFFFFFFFFFFFFFFFFF0F3607E35E0B0000020000000000803F0000803F1000393A33382E30305E32313A372E32305E3607E34F0B0000050000000000803F0000803F1000393A33382E30305E32313A372E32305E1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF1680FFFEFFFFFF04000000";

    [Fact]
    public void CapturedGasRetainsCompletedStacksAtMissingRootScalar()
    {
        var world=new Net7WorldState();
        var cloud=world.MaterializeCreate(new Net7CreateUpdate(99,1834,1,0x26,0,0,0));
        var codec=new Net7AuxDataCodec();
        var bytes=Convert.FromHexString(GasDelta);
        var envelope=Net7WorldWire.DecodeAuxDataEnvelope(0x1B,Convert.FromHexString("338701006201"+GasDelta));
        var warning=codec.ApplyObjectDelta(cloud.AuxData!,envelope.Delta,default);
        Assert.Contains("Retained completed Harvestable prefix",warning);
        var inventory=EnbSceneInventorySnapshotBuilder.Build(null,world.LocalPlayerAuxData,null,null,harvestable:cloud);
        Assert.Equal(99u,inventory.HarvestableId);
        Assert.Equal(cloud.CreationOrdinal,inventory.HarvestableOwner);
        Assert.Equal(2,inventory.Harvest.Length);
        Assert.Equal(new[]{0,1},inventory.HarvestSlotKeys);
        Assert.Equal(2910,inventory.Find(0x12,0)!.TemplateId);
        Assert.Equal(2,inventory.Find(0x12,0)!.Count);
        Assert.Equal(2895,inventory.Find(0x12,1)!.TemplateId);
        Assert.Equal(5,inventory.Find(0x12,1)!.Count);
        Assert.Empty(inventory.Loot);
        var strict=new Net7AuxDataSchemaCatalog().CreateObjectSchema(0x26)!;
        Assert.Throws<InvalidDataException>(()=>codec.Apply(strict,envelope.Delta));
        Assert.Null(strict.Resolve(4).Value);
        // An incomplete item retains only its already decoded fields.
        var truncated=new Net7AuxDataSchemaCatalog().CreateObjectSchema(0x26)!;
        Assert.NotNull(codec.ApplyObjectDelta(truncated,bytes.AsSpan(0,21),default));
        Assert.Equal(2910,truncated.Resolve(4).Value);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void CapturedTechLevelTailOnlyChangesDiagnosticClassification(int tailLength, bool compatible)
    {
        var world = new Net7WorldState();
        var cloud = world.MaterializeCreate(new Net7CreateUpdate(0x18733,1834,1,0x26,0,0,0));
        var level = cloud.AuxData!.Resolve(444);
        level.Value = 9; level.State = 2; level.UpdateRevision = 17;
        byte[] payload = Convert.FromHexString("338701006201" + GasDelta);
        Array.Resize(ref payload, 6 + 354 + tailLength);
        var baseline = new Net7AuxDataSchemaCatalog().CreateObjectSchema(0x26)!;
        baseline.Resolve(444).Value = 9; baseline.Resolve(444).State = 2; baseline.Resolve(444).UpdateRevision = 17;
        new Net7AuxDataCodec().ApplyObjectDelta(baseline, payload.AsSpan(6,354), default);
        var result = world.Apply(Packet.ForOpcode(0x1B, payload), 0);
        foreach (var expected in baseline.Properties)
        {
            var actual = cloud.AuxData.Resolve(expected.Id);
            Assert.Equal(expected.State, actual.State);
            Assert.Equal(expected.UpdateRevision, actual.UpdateRevision);
            if (expected.Value is Net7AuxDataString text)
                Assert.Equal(text.Bytes, Assert.IsType<Net7AuxDataString>(actual.Value).Bytes);
            else Assert.Equal(expected.Value, actual.Value);
        }
        Assert.Equal(compatible, result.AuxDataCompatibilityNote is not null);
        Assert.Null(result.AuxDataWarning);
        Assert.Equal(9, level.Value); Assert.Equal(2, level.State); Assert.Equal(17ul, level.UpdateRevision);
        var inventory = EnbSceneInventorySnapshotBuilder.Build(null,world.LocalPlayerAuxData,null,null,harvestable:cloud);
        Assert.Equal(2, inventory.Harvest.Length);
        Assert.Equal(2910, inventory.Find(0x12,0)!.TemplateId);
        Assert.Equal(2, inventory.Find(0x12,0)!.Count);
        Assert.Equal(2895, inventory.Find(0x12,1)!.TemplateId);
        Assert.Equal(5, inventory.Find(0x12,1)!.Count);
        Assert.False(cloud.AuxDataAudit!.LargestAccepted);
        Assert.Equal(compatible, cloud.AuxDataAudit.LargestRetailCompatiblePartial);
        if (compatible)
        {
            var issues = new EnbNext.Engine.LiveIssueLog();
            issues.ObserveDiagnostic($"WORLD-09 0x001B compatibility: {result.AuxDataCompatibilityNote}");
            Assert.Equal(0, issues.Count);
        }
    }

    [Fact]
    public void HarvestableTechLevelPresenceBitControlsConsumption()
    {
        var codec = new Net7AuxDataCodec();
        var schema = new Net7AuxDataSchemaCatalog().CreateObjectSchema(0x26)!;
        byte[] declared = Convert.FromHexString(GasDelta)[..354];
        // Root A6 02: value map A selects Resources + TechLevel; state-two
        // map 2 selects Resources. Clearing bit 7 removes TechLevel only.
        declared[1] &= 0x7f;
        Assert.Null(codec.ApplyObjectDelta(schema, declared, default));
        Assert.Null(schema.Resolve(444).Value);
        declared[1] |= 0x80;
        Assert.Contains("Harvestable.TechLevel", codec.ApplyObjectDelta(schema, declared, default));
        Assert.Null(codec.ApplyObjectDelta(schema, Convert.FromHexString(GasDelta), default));
        Assert.Equal(4, schema.Resolve(444).Value);
    }

    [Fact]
    public void HarvestWindowFollowsNativeOpenInterfaceAndSelectedTarget()
    {
        var world=new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(99,1834,1,0x26,0,0,0));
        var game=new Net7GameplayState(resolveWorldObject:id=>world.Objects.FindFirst(id));
        game.SetLocalSelectedTarget(99);
        game.Apply(Packet.ForOpcode(0x66,new byte[8]));
        Assert.Equal(99u,game.CurrentHarvestableId);
        Assert.Null(game.CurrentHulkId);
        game.Apply(Packet.ForOpcode(0x66,new byte[]{0,0,0,0,1,0,0,0}));
        Assert.Equal(99u,game.CurrentHarvestableId);
        game.Apply(Packet.ForOpcode(0x66,new byte[]{1,0,0,0,0,0,0,0}));
        Assert.Null(game.CurrentHarvestableId);
        game.SetLocalSelectedTarget(100);
        game.Apply(Packet.ForOpcode(0x66,new byte[8]));
        Assert.Null(game.CurrentHarvestableId);
    }
}
