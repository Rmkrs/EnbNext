using System.Buffers.Binary;
using System.Reflection;
using EnbNext.Net7;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Mfg05DirectAuditTests
{
    private const uint Player = 0x11223344;
    private const uint LabId = 0x55667788;

    [Fact]
    public void ActionRequestIsExactlyTwoBigEndianDwordsAndOnlyCurrentIdsZeroThroughThreeAreEmitted()
    {
        foreach (uint actionId in new uint[] { 0, 1, 2, 3 })
        {
            byte[] payload = Net7GameplayWire.BuildManufacturingAction(Player, actionId);
            Assert.Equal(8, payload.Length);
            Assert.Equal(Player, BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4)));
            Assert.Equal(actionId, BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4, 4)));

            Net7ManufacturingActionRequest decoded = Net7GameplayWire.DecodeManufacturingActionRequest(payload);
            Assert.Equal(Player, decoded.PlayerId);
            Assert.Equal(actionId, decoded.ActionId);
        }

        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeManufacturingActionRequest(new byte[7]));
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeManufacturingActionRequest(new byte[9]));
    }

    [Fact]
    public async Task NumericInbound007ECannotMasqueradeAsActionAcknowledgement()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        int delivered = 0;
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x007E)
                delivered++;
        };

        MethodInfo dispatch = typeof(Net7Session).GetMethod(
            "DeliverGameplayPacketCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Task valid = (Task)dispatch.Invoke(session,
            [Packet.ForOpcode(0x007E, Net7GameplayWire.BuildManufacturingAction(Player, 2)), "decoded-inner", false, CancellationToken.None])!;
        await valid;
        Assert.Equal(0, delivered);

        Task malformed = (Task)dispatch.Invoke(session,
            [Packet.ForOpcode(0x007E, new byte[7]), "decoded-inner", false, CancellationToken.None])!;
        await malformed;
        Assert.Equal(0, delivered);
    }

    [Fact]
    public void ManufactureAndRefineryActionPrerequisitesRemainContextSpecific()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);
        Assert.False(domain.CanSendManualAction(2));
        Apply(world, ("ManufacturingLab.KnownFormula.0.ItemID", I32(500)));
        domain.RecordManufactureItemSent(500);
        Assert.True(domain.CanSendManualAction(2));
        Assert.False(domain.CanSendManualAction(3));

        domain.RecordModeSent(Net7ManufacturingMode.Refining);
        Assert.False(domain.CanSendManualAction(2));
        Assert.False(domain.CanSendManualAction(3));
        domain.RecordRefineryItemSent(600);
        Assert.True(domain.CanSendManualAction(2));
        Assert.True(domain.CanSendManualAction(3));
    }

    [Fact]
    public void AttemptResultVocabularyIsExactly0EThrough11AndValidIsNotAResult()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        foreach ((int value, bool expected) in new[]
        {
            (0x0D, false), (0x0E, true), (0x0F, true), (0x10, true), (0x11, true), (0x12, false),
        })
        {
            Apply(world, ("ManufacturingLab.Validity", I32(value)));
            Assert.Equal(expected, domain.CurrentLab!.IsAttemptResult);
        }
    }

    [Fact]
    public void AnalysisInitialTimerGatesResultAndActionOneDoesNotSelfRearm()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        domain.RecordModeSent(Net7ManufacturingMode.Analyze);
        Apply(world, ("ManufacturingLab.Validity", I32(0x0D)));
        Assert.True(domain.CanSendManualAction(2));
        domain.RecordActionSent(2);
        Assert.Equal(2000u, domain.ActionCountdownMilliseconds);

        Apply(world, ("ManufacturingLab.Validity", I32(0x0F)));
        _ = domain.ReconcileAuthoritativeState();
        Assert.Equal(Net7ManufacturingActionContext.Analyze, domain.ActionContext);
        Assert.Equal(2000u, domain.ActionCountdownMilliseconds);

        Assert.True(domain.AdvanceActionTimer(2000));
        Assert.Equal(0u, domain.ActionCountdownMilliseconds);
        _ = domain.ReconcileAuthoritativeState();
        Assert.Equal(Net7ManufacturingActionContext.Idle, domain.ActionContext);
        Assert.True(domain.AnalysisFollowUpArmed);
        Assert.Equal(2000u, domain.ActionCountdownMilliseconds);

        domain.RecordActionSent(1);
        Assert.Equal(2000u, domain.ActionCountdownMilliseconds);
        Assert.True(domain.AdvanceActionTimer(2000));
        domain.RecordActionSent(1);
        Assert.Equal(0u, domain.ActionCountdownMilliseconds);
        Assert.False(domain.AdvanceActionTimer(2000));
    }

    [Fact]
    public void ProvenanceOwns007EAndNoActionSpecificRetrySurfaceExists()
    {
        Assert.Contains("MFG-05", Net7GameplayProvenance.GetOwners(0x007E));
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "EnbNext.Net7", "Net7Session.cs"));
        Assert.DoesNotContain("ManufacturingActionRetry", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RetryManufacturingAction", source, StringComparison.Ordinal);
    }

    private static (Net7WorldState World, Net7ManufacturingDomain Domain) CreateLab()
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(LabId, 1f, 0, 0x27, 0, 0, 0));
        var domain = new Net7ManufacturingDomain(id => world.Objects.FindFirst(id));
        domain.SetActiveObject(LabId);
        return (world, domain);
    }

    private static void Apply(Net7WorldState world, params (string Path, byte[] Wire)[] values)
    {
        Net7AuxDataSchema schema = world.Objects.FindFirst(LabId)!.AuxData!;
        using var delta = new MemoryStream();
        delta.WriteByte(0);
        WriteU32(delta, checked((uint)values.Length));
        foreach ((string path, byte[] wire) in values)
        {
            Net7AuxDataProperty property = Assert.Single(schema.Properties, p => p.Path == path);
            WriteU32(delta, checked((uint)property.Id));
            delta.Write(wire);
        }
        WriteU32(delta, 0);
        WriteU32(delta, 0);

        byte[] encoded = delta.ToArray();
        byte[] envelope = new byte[6 + encoded.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(0, 4), LabId);
        BinaryPrimitives.WriteUInt16LittleEndian(envelope.AsSpan(4, 2), checked((ushort)encoded.Length));
        encoded.CopyTo(envelope, 6);
        _ = world.Apply(Packet.ForOpcode(0x001B, envelope), 0);
    }

    private static byte[] I32(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EnbNext.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate EnbNext repository root.");
    }
}
