using System.Buffers.Binary;
using System.Reflection;
using EnbNext.Net7;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Mfg08DirectAuditTests
{
    private const uint Player = 0x11223344;
    private const uint LabId = 0x55667788;

    [Fact]
    public void Type39DefaultsAndNestedGeometryMatchCurrentRetailConstructor()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        Net7AuxDataSchema schema = world.Objects.FindFirst(LabId)!.AuxData!;
        Assert.Equal("ManufacturingLab", schema.Name);
        Assert.Equal(2427, schema.Properties.Count);
        Assert.Equal(21, schema.Root.DeclaredChildCount);
        Assert.Equal(2, Find(schema, "ManufacturingLab.ManufactureTarget").DeclaredChildCount);
        Assert.Equal(6, Find(schema, "ManufacturingLab.ManufactureComponents").DeclaredChildCount);
        Assert.Equal(1, Find(schema, "ManufacturingLab.ManufactureOverride").DeclaredChildCount);
        Assert.Equal(2, Find(schema, "ManufacturingLab.PrimaryCategories").DeclaredChildCount);
        Assert.Equal(500, Find(schema, "ManufacturingLab.KnownFormula").DeclaredChildCount);
        Assert.Equal(15, Find(schema, "ManufacturingLab.PreviousAttempts").DeclaredChildCount);
        Assert.Equal(10, Find(schema, "ManufacturingLab.ManufactureTarget.0").DeclaredChildCount);
        Assert.Equal(3, Find(schema, "ManufacturingLab.KnownFormula.0").DeclaredChildCount);
        Assert.Equal(5, Find(schema, "ManufacturingLab.PrimaryCategories.0.Categories").DeclaredChildCount);
        Assert.Equal(5, Find(schema, "ManufacturingLab.PrimaryCategories.0.Categories.0.Subcategories").DeclaredChildCount);

        Net7ManufacturingLabProjection lab = domain.CurrentLab!;
        Assert.Equal("Manufacturing Lab", lab.Name);
        Assert.Equal(1, lab.Mode);
        Assert.False(lab.ModeMaterialized);
        Assert.Equal(Net7ManufacturingValidity.NoTarget, lab.Validity);
        Assert.Equal(Net7ManufacturingDifficulty.Impossible, lab.Difficulty);
        Assert.Equal(2, lab.ManufactureTargets.Count);
        Assert.Equal(6, lab.ManufactureComponents.Count);
        Assert.Single(lab.ManufactureOverride);
        Assert.All(lab.ManufactureTargets.Concat(lab.ManufactureComponents).Concat(lab.ManufactureOverride), item =>
        {
            Assert.Equal(-1, item.ItemTemplateId);
            Assert.Equal(0, item.StackCount);
            Assert.Equal(ulong.MaxValue, item.Price);
            Assert.Equal(0.0f, item.AverageCost);
            Assert.Equal(-1.0f, item.Structure);
            Assert.Equal(-1.0f, item.Quality);
        });
        Assert.Empty(lab.KnownFormula);
        Assert.Empty(lab.PreviousAttempts);
        Assert.Equal(-1, lab.CurrentItemCategory);
        Assert.Equal(1.0f, lab.SuccessProbability);
        Assert.Equal(0.0f, lab.CriticalSuccessProbability);
        Assert.Equal(1.0f, lab.ExpectedQuality);
        Assert.Equal(1.0f, lab.MinimumQuality);
        Assert.Equal(1.0f, lab.MaximumQuality);
        Assert.Equal(0, lab.AdditionalIterations);
        Assert.Equal(0x1FF, lab.TechLevelFilterBitfield);
    }

    [Fact]
    public void KnownFormulaMembershipAloneDefinesMappedState()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        Apply(world,
            ("ManufacturingLab.PreviousAttempts.0.ItemName", Lp("Attempted")),
            ("ManufacturingLab.PreviousAttempts.0.ItemID", I32(77)),
            ("ManufacturingLab.PreviousAttempts.0.TechLevel", I32(5)));

        Net7ManufacturingLabProjection lab = domain.CurrentLab!;
        Assert.False(lab.IsMapped(77));
        Assert.Equal(77, Assert.Single(lab.PreviousAttempts).ItemId);

        Apply(world,
            ("ManufacturingLab.KnownFormula.0.ItemName", Lp("Known")),
            ("ManufacturingLab.KnownFormula.0.ItemID", I32(77)),
            ("ManufacturingLab.KnownFormula.0.TechLevel", I32(5)));

        lab = domain.CurrentLab!;
        Assert.True(lab.IsMapped(77));
        Assert.Equal(77, Assert.Single(lab.KnownFormula).ItemId);
    }

    [Fact]
    public void QualityPresentationMatchesRetailClampingWithoutMutatingAuthoritativeFloats()
    {
        (Net7WorldState world, Net7ManufacturingDomain domain) = CreateLab();
        Apply(world,
            ("ManufacturingLab.ExpectedQuality", F32(4.5f)),
            ("ManufacturingLab.MinimumQuality", F32(-0.5f)),
            ("ManufacturingLab.MaximumQuality", F32(4.0f)));

        Net7ManufacturingLabProjection lab = domain.CurrentLab!;
        Assert.Equal(4.5f, lab.ExpectedQuality);
        Assert.Equal(-0.5f, lab.MinimumQuality);
        Assert.Equal(4.0f, lab.MaximumQuality);
        Assert.Equal(300.0f, lab.ExpectedQualityPercent!.Value);
        Assert.True(lab.QualityRangePercent.HasValue);
        Assert.Equal((0.0f, 300.0f), lab.QualityRangePercent.Value);

        Apply(world,
            ("ManufacturingLab.MinimumQuality", F32(2.5f)),
            ("ManufacturingLab.MaximumQuality", F32(1.5f)));
        lab = domain.CurrentLab!;
        Assert.Equal(2.5f, lab.MinimumQuality);
        Assert.Equal(1.5f, lab.MaximumQuality);
        Assert.True(lab.QualityRangePercent.HasValue);
        Assert.Equal((150.0f, 150.0f), lab.QualityRangePercent.Value);
        Assert.Equal(150.0f, lab.MinimumQualityPercent!.Value);
        Assert.Equal(150.0f, lab.MaximumQualityPercent!.Value);

        Apply(world,
            ("ManufacturingLab.ExpectedQuality", F32(-1.0f)),
            ("ManufacturingLab.MinimumQuality", F32(-1.0f)),
            ("ManufacturingLab.MaximumQuality", F32(1.0f)));
        lab = domain.CurrentLab!;
        Assert.Null(lab.ExpectedQualityPercent);
        Assert.Null(lab.QualityRangePercent);
        Assert.Null(lab.MinimumQualityPercent);
        Assert.Null(lab.MaximumQualityPercent);
    }

    [Fact]
    public void ValidityAndDifficultyVocabulariesAreExactAndComplete()
    {
        Assert.Equal(Enumerable.Range(0, 0x13), Enum.GetValues<Net7ManufacturingValidity>().Select(value => (int)value));
        Assert.Equal(Enumerable.Range(0, 7), Enum.GetValues<Net7ManufacturingDifficulty>().Select(value => (int)value));
    }

    [Fact]
    public void Full07Excludes007DFactoryResidueFromIntegratedSurface()
    {
        Assert.Empty(Net7GameplayProvenance.GetOwners(0x007D));
        Assert.False(Net7GameplayProvenance.IsImplemented(0x007D));

        string session = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "EnbNext.Net7", "Net7Session.cs"));
        Assert.DoesNotContain("case 0x007D:", session, StringComparison.Ordinal);
        Assert.DoesNotContain("TrySendGameplayAsync(0x007D", session, StringComparison.Ordinal);

        string wire = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "EnbNext.Net7", "Gameplay", "Net7GameplayWire.cs"));
        Assert.DoesNotContain("Net7ManufacturingSetSlotRequest", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("DecodeManufacturingSetSlotRequest", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildManufacturingSetSlot", wire, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DormantNumericInbound007DCannotMasqueradeAsManufacturingWorkflow()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        int delivered = 0;
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x007D)
                delivered++;
        };

        MethodInfo dispatch = typeof(Net7Session).GetMethod(
            "DeliverGameplayPacketCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        byte[] validPayload = Convert.FromHexString("11223344FFFFFFFF00000007");
        Task valid = (Task)dispatch.Invoke(session,
            [Packet.ForOpcode(0x007D, validPayload), "decoded-inner", false, CancellationToken.None])!;
        await valid;
        Assert.Equal(0, delivered);

        Task malformed = (Task)dispatch.Invoke(session,
            [Packet.ForOpcode(0x007D, new byte[11]), "decoded-inner", false, CancellationToken.None])!;
        await malformed;
        Assert.Equal(0, delivered);
    }

    private static Net7AuxDataProperty Find(Net7AuxDataSchema schema, string path)
        => Assert.Single(schema.Properties, property => property.Path == path);

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

    private static byte[] F32(float value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(value));
        return bytes;
    }

    private static byte[] Lp(string value)
    {
        byte[] text = System.Text.Encoding.Latin1.GetBytes(value);
        byte[] bytes = new byte[2 + text.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)text.Length));
        text.CopyTo(bytes, 2);
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
