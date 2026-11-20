using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Rendering;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6TLiveSceneAmbientTests
{
    [Fact]
    public void ServerParametersCaptureExactSplinePrefixAtOrbitalManagerLoadTime()
    {
        var world = new Net7WorldState();
        byte[] before = BuildRuntimeType3Update("SkyEmissive.Default_R", 0.25f);
        byte[] after = BuildRuntimeType3Update("SkyEmissive.Default_R", 0.75f);

        world.Apply(Packet.ForOpcode(0x0038, WrapSplinePacketData(before)), 0);
        world.Apply(Packet.ForOpcode(0x0042, ServerParameters(sectorNum: 1065)), 0);
        world.Apply(Packet.ForOpcode(0x0038, WrapSplinePacketData(after)), 0);

        Assert.Equal(1, world.ServerParametersSplineUpdateCount);
        Assert.Equal(2, world.SplineUpdates.Count);

        EnbSceneSnapshot snapshot = new EnbPresentationSnapshotBuilder().Build(world, assets: null, clientTime: 1234);
        Assert.NotNull(snapshot.Environment);
        Assert.Equal(1, snapshot.Environment!.SplineUpdateCountAtLoad);
        Assert.Equal(2, snapshot.SplineUpdates.Length);
    }

    [Fact]
    public void NewServerParametersResampleAgainstThenCurrentSplinePrefix()
    {
        var world = new Net7WorldState();
        world.Apply(Packet.ForOpcode(0x0038, WrapSplinePacketData(BuildRuntimeType3Update("Runtime.A_R", 0.1f))), 0);
        world.Apply(Packet.ForOpcode(0x0042, ServerParameters(sectorNum: 7)), 0);
        Assert.Equal(1, world.ServerParametersSplineUpdateCount);

        world.Apply(Packet.ForOpcode(0x0038, WrapSplinePacketData(BuildRuntimeType3Update("Runtime.B_R", 0.2f))), 0);
        world.Apply(Packet.ForOpcode(0x0042, ServerParameters(sectorNum: 8)), 0);

        Assert.Equal(2, world.ServerParametersSplineUpdateCount);
        EnbSceneSnapshot snapshot = new EnbPresentationSnapshotBuilder().Build(world, assets: null, clientTime: 0);
        EnbSceneEnvironmentSnapshot environment = Assert.IsType<EnbSceneEnvironmentSnapshot>(snapshot.Environment);
        Assert.Equal(2, environment.SplineUpdateCountAtLoad);
        Assert.Equal((uint)8, environment.SectorNum);
    }

    [Fact]
    public void SkyEmissiveIsSampledAtTimeZeroFromLoadTimeDatabaseState()
    {
        EnbRetailScalarSplineDatabase splines = EnbRetailScalarSplineDatabase.ParseWsd(BuildWsd(
            CurveRecord("SkyEmissive.Custom_R", (0.2f, 0f), (0.8f, 1f)),
            CurveRecord("SkyEmissive.Custom_G", (0.3f, 0f), (0.7f, 1f)),
            CurveRecord("SkyEmissive.Custom_B", (0.4f, 0f), (0.6f, 1f))));
        var scene = new EnbRetailOrbitalSceneDefinition(
            "LightColor.Earth",
            "SkyColor.Earth",
            "SkyEmissive.Custom",
            Array.Empty<EnbRetailOrbitalDefinition>());

        Assert.True(EnbRetailOrbitalFogEvaluator.TrySampleSkyEmissiveAtLoad(
            scene, splines, out Vector3 rgb, out string? diagnostic), diagnostic);
        AssertClose(new Vector3(0.2f, 0.3f, 0.4f), rgb);
    }

    [Fact]
    public void LaterSplineMutationChangesCurrentDatabaseButNotCapturedSkyEmissiveValue()
    {
        EnbRetailScalarSplineDatabase splines = EnbRetailScalarSplineDatabase.ParseWsd(BuildWsd(
            CurveRecord("SkyEmissive.Default_R", (0.1f, 0f)),
            CurveRecord("SkyEmissive.Default_G", (0.2f, 0f)),
            CurveRecord("SkyEmissive.Default_B", (0.3f, 0f))));
        var scene = new EnbRetailOrbitalSceneDefinition(
            "LightColor.Earth",
            "SkyColor.Earth",
            "SkyEmissive.Default",
            Array.Empty<EnbRetailOrbitalDefinition>());

        splines.ApplyRuntimeUpdate(BuildRuntimeType3Update("SkyEmissive.Default_R", 0.25f));
        Assert.True(EnbRetailOrbitalFogEvaluator.TrySampleSkyEmissiveAtLoad(
            scene, splines, out Vector3 cached, out string? cachedDiagnostic), cachedDiagnostic);

        splines.ApplyRuntimeUpdate(BuildRuntimeType3Update("SkyEmissive.Default_R", 0.75f));
        Assert.True(splines.TrySample("SkyEmissive.Default", 'R', 0f, out float currentRed));

        Assert.Equal(0.25f, cached.X);
        Assert.Equal(0.75f, currentRed);
    }

    [Fact]
    public void ModesZeroAndFiveUseCachedSkyEmissiveAsGlobalAmbient()
    {
        Vector3 emissive = new(0.2f, 0.3f, 0.4f);
        Vector3 fog = new(0.8f, 0.7f, 0.6f);

        Assert.True(EnbRetailOrbitalFogEvaluator.TryComputeCurrentRetailGlobalAmbient(
            0, fog, emissive, out Vector3 modeZero, out string? zeroDiagnostic), zeroDiagnostic);
        Assert.True(EnbRetailOrbitalFogEvaluator.TryComputeCurrentRetailGlobalAmbient(
            5, fog, emissive, out Vector3 modeFive, out string? fiveDiagnostic), fiveDiagnostic);

        Assert.Equal(emissive, modeZero);
        Assert.Equal(emissive, modeFive);
    }

    [Fact]
    public void ModeOneOrdinaryPassAddsFogAndClampsOnlyUpperSide()
    {
        Assert.True(EnbRetailOrbitalFogEvaluator.TryComputeCurrentRetailGlobalAmbient(
            1,
            new Vector3(0.4f, 0.5f, 0.1f),
            new Vector3(0.8f, -0.75f, 0.2f),
            out Vector3 ambient,
            out string? diagnostic), diagnostic);

        AssertClose(new Vector3(1f, -0.25f, 0.3f), ambient);
    }

    [Fact]
    public void CurrentRetailAmbientPackingTruncatesWithoutFogClamp()
    {
        Assert.Equal(0x7E_7F_3Fu, EnbRetailOrbitalFogEvaluator.PackCurrentRetailAmbientRgb24(
            new Vector3(1.5f, 0.5f, 0.25f)));
        Assert.Equal(0x7F_1F_3Fu, EnbRetailOrbitalFogEvaluator.PackCurrentRetailAmbientRgb24(
            new Vector3(0.5f, 0.125f, 0.25f)));
    }

    [Fact]
    public void CurrentRetailIniStripsInlineSemicolonBeforeOrbitalValueParse()
    {
        EnbRetailOrbitalSceneDefinition scene = EnbRetailOrbitalSceneDefinition.Parse(
            EnbRetailIniDocument.Parse("""
                [ORBITALS]
                NUM_ORBITALS=0
                SKYEMISSIVE=SkyEmissive.Red Ambient; authored comment
                """));

        Assert.Equal("SkyEmissive.Red Ambient", scene.SkyEmissiveSpline);
    }

    [Fact]
    public void CurrentRetailWsdRootDoesNotRequireContainerSizeFlag()
    {
        byte[] bytes = BuildWsd(false,
            CurveRecord("SkyEmissive.NoContainerFlag_R", (0.375f, 0f)));

        Assert.True(EnbRetailScalarSplineDatabase.BeginsWithCurrentRetailRoot(bytes));
        EnbRetailScalarSplineDatabase splines = EnbRetailScalarSplineDatabase.ParseWsd(bytes);
        Assert.True(splines.TrySample("SkyEmissive.NoContainerFlag", 'R', 0f, out float value));
        Assert.Equal(0.375f, value);
    }

    [Fact]
    public void CurrentRetailWsdStringTableAndRecordsAreRootSiblings()
    {
        byte[] bytes = BuildWsd(
            CurveRecord("SkyEmissive.FlatSibling_R", (0.4375f, 0f)));

        uint rootPayloadSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) & 0x7fff_ffffu;
        Assert.Equal(16u, rootPayloadSize);
        int stringHeaderOffset = checked(8 + (int)rootPayloadSize);
        Assert.Equal(0x808u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(stringHeaderOffset, 4)));

        EnbRetailScalarSplineDatabase splines = EnbRetailScalarSplineDatabase.ParseWsd(bytes);
        Assert.True(splines.TrySample("SkyEmissive.FlatSibling", 'R', 0f, out float value));
        Assert.Equal(0.4375f, value);
    }

    [Fact]
    public void OrbitalIniUsesCurrentRetailLooseSearchWinnerBeforeDataCommon()
    {
        string root = Path.Combine(Path.GetTempPath(), $"enbnext-r6t-orbital-resource-{Guid.NewGuid():N}");
        try
        {
            string release = Path.Combine(root, "release");
            string ini = Path.Combine(root, "Data", "client", "ini");
            string mixfiles = Path.Combine(root, "Data", "client", "mixfiles");
            string common = Path.Combine(root, "Data", "common");
            Directory.CreateDirectory(release);
            Directory.CreateDirectory(ini);
            Directory.CreateDirectory(mixfiles);
            Directory.CreateDirectory(common);
            File.WriteAllBytes(Path.Combine(release, "client.exe"), [0x4D, 0x5A]);
            File.WriteAllText(Path.Combine(ini, "basset.ini"), string.Empty);

            string releaseOrbital = Path.Combine(release, "orbital1065.ini");
            File.WriteAllText(releaseOrbital, "[ORBITALS]\nNUM_ORBITALS=0\n");
            File.WriteAllText(Path.Combine(common, "orbital1065.ini"), "[ORBITALS]\nNUM_ORBITALS=1\n");

            EnbInstalledAssetPaths paths = EnbInstalledAssetLocator.TryResolveRoot(root)
                ?? throw new Xunit.Sdk.XunitException("Synthetic retail root was not resolved.");

            Assert.True(paths.TryResolveOrbitalIniPath(1065, out string resolved));
            Assert.Equal(releaseOrbital, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SceneFogDataUsesCurrentRetailLooseSearchWinnerBeforeDataCommon()
    {
        string root = Path.Combine(Path.GetTempPath(), $"enbnext-r6t-resource-{Guid.NewGuid():N}");
        try
        {
            string release = Path.Combine(root, "release");
            string ini = Path.Combine(root, "Data", "client", "ini");
            string mixfiles = Path.Combine(root, "Data", "client", "mixfiles");
            string common = Path.Combine(root, "Data", "common");
            Directory.CreateDirectory(release);
            Directory.CreateDirectory(ini);
            Directory.CreateDirectory(mixfiles);
            Directory.CreateDirectory(common);
            File.WriteAllBytes(Path.Combine(release, "client.exe"), [0x4D, 0x5A]);
            File.WriteAllText(Path.Combine(ini, "basset.ini"), string.Empty);

            string releaseSpline = Path.Combine(release, "splines.wsd");
            File.WriteAllBytes(releaseSpline, BuildWsd(
                CurveRecord("SkyEmissive.SearchWinner_R", (0.625f, 0f))));
            File.WriteAllBytes(Path.Combine(common, "splines.wsd"), [0x00, 0x01, 0x02, 0x03]);

            EnbInstalledAssetPaths paths = EnbInstalledAssetLocator.TryResolveRoot(root)
                ?? throw new Xunit.Sdk.XunitException("Synthetic retail root was not resolved.");
            EnbRetailSceneFogData data = EnbRetailSceneFogData.Load(paths);

            Assert.Equal(releaseSpline, data.SplineSourcePath);
            Assert.True(data.Splines.TrySample("SkyEmissive.SearchWinner", 'R', 0f, out float value));
            Assert.Equal(0.625f, value);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectedEarlierRetailLooseWinnerStartsEmptyAndDoesNotFallThroughAfterOpen()
    {
        string root = Path.Combine(Path.GetTempPath(), $"enbnext-r6t-resource-{Guid.NewGuid():N}");
        try
        {
            string release = Path.Combine(root, "release");
            string ini = Path.Combine(root, "Data", "client", "ini");
            string mixfiles = Path.Combine(root, "Data", "client", "mixfiles");
            string common = Path.Combine(root, "Data", "common");
            Directory.CreateDirectory(release);
            Directory.CreateDirectory(ini);
            Directory.CreateDirectory(mixfiles);
            Directory.CreateDirectory(common);
            File.WriteAllBytes(Path.Combine(release, "client.exe"), [0x4D, 0x5A]);
            File.WriteAllText(Path.Combine(ini, "basset.ini"), string.Empty);

            string releaseSpline = Path.Combine(release, "splines.wsd");
            File.WriteAllBytes(releaseSpline, [0x10, 0x20, 0x30, 0x40]);
            File.WriteAllBytes(Path.Combine(common, "splines.wsd"), BuildWsd(
                CurveRecord("SkyEmissive.ShouldNotWin_R", (0.75f, 0f))));

            EnbInstalledAssetPaths paths = EnbInstalledAssetLocator.TryResolveRoot(root)
                ?? throw new Xunit.Sdk.XunitException("Synthetic retail root was not resolved.");
            EnbRetailSceneFogData data = EnbRetailSceneFogData.Load(paths);

            Assert.Equal(releaseSpline, data.SplineSourcePath);
            Assert.NotNull(data.BaseLoadDiagnostic);
            Assert.Equal(0, data.Splines.Count);
            Assert.False(data.Splines.TrySample("SkyEmissive.ShouldNotWin", 'R', 0f, out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectedBaseDatabaseCanBePopulatedByRuntimeSplineUpdatesBeforeOrbitalLoad()
    {
        EnbRetailScalarSplineDatabase splines = EnbRetailScalarSplineDatabase.CreateEmpty();
        splines.ApplyRuntimeUpdate(BuildRuntimeType3Update("SkyEmissive.Runtime_R", 0.2f));
        splines.ApplyRuntimeUpdate(BuildRuntimeType3Update("SkyEmissive.Runtime_G", 0.3f));
        splines.ApplyRuntimeUpdate(BuildRuntimeType3Update("SkyEmissive.Runtime_B", 0.4f));

        var scene = new EnbRetailOrbitalSceneDefinition(
            "LightColor.Earth",
            "SkyColor.Earth",
            "SkyEmissive.Runtime",
            Array.Empty<EnbRetailOrbitalDefinition>());

        Assert.True(EnbRetailOrbitalFogEvaluator.TrySampleSkyEmissiveAtLoad(
            scene, splines, out Vector3 rgb, out string? diagnostic), diagnostic);
        AssertClose(new Vector3(0.2f, 0.3f, 0.4f), rgb);
    }

    [Fact]
    public void MissingOrbitalIniUsesExactResetStateWithoutSplineAssets()
    {
        EnbSceneEnvironmentSnapshot environment = Environment(mode: 0, fogNear: 100000f, fogFar: 100000f);

        Assert.True(EnbRetailOrbitalFogEvaluator.TryEvaluateMissingOrbitalIniResetState(
            environment,
            out EnbRetailOrbitalFogEvaluation evaluation,
            out string? diagnostic), diagnostic);

        Assert.False(evaluation.SceneFog.Enabled);
        Assert.Equal(0u, evaluation.SceneFog.SceneColorRgb24);
        Assert.Equal(0u, evaluation.GlobalAmbientRgb24);
        Assert.Equal(Vector3.Zero, evaluation.FogRgb);
        Assert.Equal(0, evaluation.OrbitalCount);
        Assert.Equal(0, evaluation.EmittingOrbitalCount);
        Assert.Equal(0, evaluation.OccludedSampleCount);
    }

    [Fact]
    public void MissingOrbitalIniKeepsForcedFogEnabledButBlackInModeFive()
    {
        EnbSceneEnvironmentSnapshot environment = Environment(mode: 5, fogNear: 100f, fogFar: 300f);

        Assert.True(EnbRetailOrbitalFogEvaluator.TryEvaluateMissingOrbitalIniResetState(
            environment,
            out EnbRetailOrbitalFogEvaluation evaluation,
            out string? diagnostic), diagnostic);

        Assert.True(evaluation.SceneFog.Enabled);
        Assert.Equal(100f, evaluation.SceneFog.Start);
        Assert.Equal(300f, evaluation.SceneFog.End);
        Assert.Equal(0u, evaluation.SceneFog.SceneColorRgb24);
        Assert.Equal(0u, evaluation.GlobalAmbientRgb24);
        Assert.Equal(0, evaluation.OrbitalCount);
    }

    private static EnbSceneEnvironmentSnapshot Environment(int? mode, float fogNear, float fogFar)
        => new(
            BaseAsset: 0,
            SwapBackdrop: false,
            FinalRawValue: 7,
            RawServerParameters: System.Collections.Immutable.ImmutableArray<byte>.Empty,
            BaseAssetReference: null,
            FogNear: fogNear,
            FogFar: fogFar,
            LightBackdrop: false,
            FogBackdrop: false,
            BackdropFogNear: 0f,
            BackdropFogFar: 0f,
            PresentationMode: mode,
            SplineUpdateCountAtLoad: 0);

    private static byte[] ServerParameters(uint sectorNum)
    {
        byte[] payload = new byte[Net7PresentationWire.ServerParametersPayloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(Net7PresentationWire.ServerParametersSectorNumOffset, 4),
            sectorNum);
        return payload;
    }

    private static byte[] WrapSplinePacketData(byte[] data)
    {
        byte[] payload = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), checked((uint)data.Length));
        data.CopyTo(payload, 4);
        return payload;
    }

    private static byte[] BuildRuntimeType3Update(string name, float value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);
        writer.Write((ushort)1);
        writer.Write(EnbRetailScalarSplineDatabase.ComputeCurrentRetailCrc32(name));
        writer.Write((byte)3);
        writer.Write((byte)0);
        writer.Write((uint)1);
        writer.Write(value);
        writer.Write(0.5f);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildWsd(params byte[][] records)
        => BuildWsd(true, records);

    private static byte[] BuildWsd(bool rootContainer, params byte[][] records)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);

        using (var headerStream = new MemoryStream())
        {
            using var header = new BinaryWriter(headerStream, Encoding.Latin1, leaveOpen: true);
            header.Write(0x100u);
            header.Write((uint)records.Length);
            header.Write(0u);
            header.Write(0u);
            header.Flush();
            WriteChunk(writer, 0x800u, headerStream.ToArray(), container: rootContainer);
        }

        // Current SplineDatabaseParse closes 0x800 before opening 0x808.
        // The string table and all records are top-level sibling chunks.
        WriteChunk(writer, 0x808u, new byte[4], container: false);
        foreach (byte[] record in records)
            WriteChunk(writer, 0x810u, record, container: false);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] CurveRecord(string name, params (float Value, float Time)[] keys)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);
        byte[] nameBytes = [.. Encoding.Latin1.GetBytes(name), 0];
        writer.Write(3u);
        writer.Write((uint)nameBytes.Length);
        writer.Write(new byte[0x24 - 8]);
        writer.Write(nameBytes);
        writer.Write((byte)0);
        writer.Write((uint)keys.Length);
        foreach ((float value, float time) in keys)
        {
            writer.Write(value);
            writer.Write(time);
            writer.Write(0f);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteChunk(BinaryWriter writer, uint type, byte[] payload, bool container)
    {
        writer.Write(type);
        writer.Write((uint)payload.Length | (container ? 0x8000_0000u : 0u));
        writer.Write(payload);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, float epsilon = 1e-6f)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, epsilon);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, epsilon);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, epsilon);
    }
}
