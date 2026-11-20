using System.Buffers.Binary;
using System.Reflection;
using EnbNext.Net7;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Mfg07DirectAuditTests
{
    private const uint Player = 0x11223344;
    private const uint LabId = 0x55667788;

    [Fact]
    public void FilterRequestIsExactlyNineBytesWithBigEndianDwordsAndNormalizedMode()
    {
        byte[] enabled = Net7GameplayWire.BuildManufacturingFilter(Player, true, 0x001);
        Assert.Equal(Convert.FromHexString("112233440100000001"), enabled);

        Net7ManufacturingFilterRequest decoded = Net7GameplayWire.DecodeManufacturingFilterRequest(enabled);
        Assert.Equal(Player, decoded.PlayerId);
        Assert.True(decoded.Enable);
        Assert.Equal(0x001u, decoded.BitField);

        byte[] nonCanonicalInbound = Convert.FromHexString("112233447F00000100");
        Net7ManufacturingFilterRequest normalized = Net7GameplayWire.DecodeManufacturingFilterRequest(nonCanonicalInbound);
        Assert.True(normalized.Enable);
        Assert.Equal(0x100u, normalized.BitField);

        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeManufacturingFilterRequest(new byte[8]));
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeManufacturingFilterRequest(new byte[10]));
    }

    [Fact]
    public void CurrentProducerMasksAreSingleTechLevelsOneThroughNineOrAll()
    {
        uint[] singles = Enumerable.Range(0, 9).Select(i => 1u << i).ToArray();
        Assert.Equal(new uint[] { 0x001, 0x002, 0x004, 0x008, 0x010, 0x020, 0x040, 0x080, 0x100 }, singles);
        Assert.Equal(0x1FFu, singles.Aggregate(0u, (mask, bit) => mask | bit));
    }

    [Fact]
    public void UserIntentCanReverseBeforeAuthoritativeAuxDataCatchesUp()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        Apply(world, ("ManufacturingLab.TechLevelFilterBitfield", I32(0x1FE)));
        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);
        Assert.Equal(0x1FEu, domain.LocalTechLevelFilterBitfield);

        Assert.True(domain.ShouldSendFilter(true, 0x001));
        domain.RecordFilterSent(true, 0x001);
        Assert.Equal(0x1FFu, domain.LocalTechLevelFilterBitfield);

        Assert.True(domain.ShouldSendFilter(false, 0x001));
        domain.RecordFilterSent(false, 0x001);
        Assert.Equal(0x1FEu, domain.LocalTechLevelFilterBitfield);
    }

    [Fact]
    public void UnchangedAuthoritativeStateDoesNotOverwritePendingLocalIntentButChangedAuxDataResynchronizesIt()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        Apply(world, ("ManufacturingLab.TechLevelFilterBitfield", I32(0x1FE)));
        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);

        domain.RecordFilterSent(true, 0x001);
        Assert.Equal(0x1FFu, domain.LocalTechLevelFilterBitfield);

        Apply(world, ("ManufacturingLab.NegotiatedCost", U64(123)));
        _ = domain.ReconcileAuthoritativeState();
        Assert.Equal(0x1FFu, domain.LocalTechLevelFilterBitfield);

        Apply(world, ("ManufacturingLab.TechLevelFilterBitfield", I32(0x1FD)));
        _ = domain.ReconcileAuthoritativeState();
        Assert.Equal(0x1FDu, domain.LocalTechLevelFilterBitfield);
    }

    [Fact]
    public async Task NumericInbound0080CannotMasqueradeAsFilterAcknowledgementOrMaterialization()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        int delivered = 0;
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x0080)
                delivered++;
        };

        MethodInfo dispatch = typeof(Net7Session).GetMethod(
            "DeliverGameplayPacketCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Task valid = (Task)dispatch.Invoke(session,
            [Packet.ForOpcode(0x0080, Net7GameplayWire.BuildManufacturingFilter(Player, true, 0x1FF)), "decoded-inner", false, CancellationToken.None])!;
        await valid;
        Assert.Equal(0, delivered);

        Task malformed = (Task)dispatch.Invoke(session,
            [Packet.ForOpcode(0x0080, new byte[8]), "decoded-inner", false, CancellationToken.None])!;
        await malformed;
        Assert.Equal(0, delivered);
    }

    [Fact]
    public void ProvenanceOwns0080AndNoSameOpcodeAckOrRetrySurfaceExists()
    {
        Assert.Contains("MFG-07", Net7GameplayProvenance.GetOwners(0x0080));
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "EnbNext.Net7", "Net7Session.cs"));
        Assert.DoesNotContain("ManufacturingFilterAck", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RetryManufacturingFilter", source, StringComparison.Ordinal);
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

    private static byte[] U64(ulong value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
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
