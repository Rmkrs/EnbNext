using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Rendering;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6SLiveSceneFogTests
{
    [Fact]
    public void ServerParametersExposeExactCurrentRetailFogOffsets()
    {
        byte[] payload = new byte[Net7PresentationWire.ServerParametersPayloadSize];
        WriteF32(payload, Net7PresentationWire.ServerParametersFogNearOffset, 123.25f);
        WriteF32(payload, Net7PresentationWire.ServerParametersFogFarOffset, 987.5f);
        payload[Net7PresentationWire.ServerParametersLightBackdropOffset] = 2;
        payload[Net7PresentationWire.ServerParametersFogBackdropOffset] = 3;
        payload[Net7PresentationWire.ServerParametersSwapBackdropOffset] = 4;
        WriteF32(payload, Net7PresentationWire.ServerParametersBackdropFogNearOffset, 44.5f);
        WriteF32(payload, Net7PresentationWire.ServerParametersBackdropFogFarOffset, 555.75f);
        BinaryPrimitives.WriteInt16LittleEndian(
            payload.AsSpan(Net7PresentationWire.ServerParametersBackdropBaseAssetOffset, 2),
            -1234);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(Net7PresentationWire.ServerParametersSectorNumOffset, 4),
            0x1122_3344u);

        Net7ServerParametersPresentationUpdate decoded = Net7PresentationWire.DecodeServerParameters(payload);

        Assert.Equal(123.25f, decoded.FogNear);
        Assert.Equal(987.5f, decoded.FogFar);
        Assert.Equal((byte)2, decoded.LightBackdrop);
        Assert.Equal((byte)3, decoded.FogBackdrop);
        Assert.Equal((byte)4, decoded.SwapBackdrop);
        Assert.Equal(44.5f, decoded.BackdropFogNear);
        Assert.Equal(555.75f, decoded.BackdropFogFar);
        Assert.Equal((short)-1234, decoded.BackdropBaseAsset);
        Assert.Equal(0x1122_3344u, decoded.SectorNum);
    }

    [Fact]
    public void ModeZeroUsesExactGreaterThanTenFogGateWhileModesOneAndFiveForceFog()
    {
        EnbSceneEnvironmentSnapshot modeZeroTen = Environment(mode: 0, fogNear: 100f, fogFar: 110f);
        Assert.True(EnbRetailOrbitalFogEvaluator.TryResolveWorldFogEnabled(modeZeroTen, out bool enabled, out _));
        Assert.False(enabled);

        EnbSceneEnvironmentSnapshot modeZeroOver = Environment(mode: 0, fogNear: 100f, fogFar: 110.0001f);
        Assert.True(EnbRetailOrbitalFogEvaluator.TryResolveWorldFogEnabled(modeZeroOver, out enabled, out _));
        Assert.True(enabled);

        Assert.True(EnbRetailOrbitalFogEvaluator.TryResolveWorldFogEnabled(Environment(1, 100f, 100f), out enabled, out _));
        Assert.True(enabled);
        Assert.True(EnbRetailOrbitalFogEvaluator.TryResolveWorldFogEnabled(Environment(5, 100f, 100f), out enabled, out _));
        Assert.True(enabled);

        Assert.False(EnbRetailOrbitalFogEvaluator.TryResolveWorldFogEnabled(Environment(2, 100f, 300f), out _, out string? diagnostic));
        Assert.NotNull(diagnostic);
        Assert.Contains("0/1/5", diagnostic!);
    }

    [Fact]
    public void CurrentRetailFogRgbPackingTruncatesInsteadOfRounding()
    {
        Assert.Equal(0x7F_1F_3Fu, EnbRetailOrbitalFogEvaluator.PackCurrentRetailFogRgb24(
            new Vector3(0.5f, 0.125f, 0.25f)));
        Assert.Equal(0xFF_00_7Fu, EnbRetailOrbitalFogEvaluator.PackCurrentRetailFogRgb24(
            new Vector3(2f, -3f, 0.5f)));
    }

    [Fact]
    public void CurrentRetailSplineCrcAndType4RebuiltTangentsAreStable()
    {
        Assert.Equal(0x8B85BF6Cu, EnbRetailScalarSplineDatabase.ComputeCurrentRetailCrc32("SkyColor.Earth_R"));
        Assert.Equal(0xE6585B87u, EnbRetailScalarSplineDatabase.ComputeCurrentRetailCrc32("SkyColor.Earth_G"));
        Assert.Equal(0x9632AF08u, EnbRetailScalarSplineDatabase.ComputeCurrentRetailCrc32("SkyColor.Earth_B"));

        byte[] wsd = BuildWsd(
            CurveRecord("Hermite.Test_R", type: 4, loop: false,
                (0f, 0f, 99f, 98f, 97f),
                (1f, 1f, 96f, 95f, 94f)));
        EnbRetailScalarSplineDatabase database = EnbRetailScalarSplineDatabase.ParseWsd(wsd);

        Assert.True(database.TrySample("Hermite.Test", 'R', 0.25f, out float sampled));
        Assert.Equal(0.1796875f, sampled, 6);

        // Current 008D5B20 uses key 1 versus the penultimate key for the
        // shared loop endpoint tangent. With a closing duplicate this produces
        // a zero endpoint tangent and the midpoint below is exactly 0.5.
        EnbRetailScalarSplineDatabase loopDatabase = EnbRetailScalarSplineDatabase.ParseWsd(BuildWsd(
            CurveRecord("Hermite.Loop_R", type: 4, loop: true,
                (0f, 0f, 9f, 8f, 7f),
                (1f, 0.5f, 6f, 5f, 4f),
                (0f, 1f, 3f, 2f, 1f))));
        Assert.True(loopDatabase.TrySample("Hermite.Loop", 'R', 0.25f, out float loopSample));
        Assert.Equal(0.5f, loopSample, 6);
    }

    [Fact]
    public void OrbitalIniUsesCurrentRetailFieldsAndDefaults()
    {
        EnbRetailOrbitalSceneDefinition scene = EnbRetailOrbitalSceneDefinition.Parse(EnbRetailIniDocument.Parse("""
            [ORBITALS]
            NUM_ORBITALS=2
            LIGHTCOLOR=LightColor.Custom
            SKYCOLOR=SkyColor.Custom

            [ORBITAL000]
            OBJECT=123
            SCALE=2.5
            HUE=0.1
            SAT=0.2
            VAL=0.3
            STARTANGLE=0.4
            RX=0.5
            RZ=0.6
            DISTANCE=777
            ORBITSPERHOUR=1.25
            PHASEPERHOUR=2.5
            EMITS_LIGHT=1
            LIGHTINTENSITY=LightIntensity.Custom
            SKYINTENSITY=SkyIntensity.Custom
            LIGHTRADIUS=0.75
            FORCE_VISIBLE=1

            [ORBITAL001]
            OBJECT=456
            """));

        Assert.Equal("LightColor.Custom", scene.LightColorSpline);
        Assert.Equal("SkyColor.Custom", scene.SkyColorSpline);
        Assert.Equal("SkyEmissive.Default", scene.SkyEmissiveSpline);
        Assert.Equal(2, scene.Orbitals.Count);

        EnbRetailOrbitalDefinition authored = scene.Orbitals[0];
        Assert.Equal(123, authored.BaseAsset);
        Assert.Equal(2.5f, authored.Scale);
        Assert.Equal(0.1f, authored.HsvH);
        Assert.Equal(0.2f, authored.HsvS);
        Assert.Equal(0.3f, authored.HsvV);
        Assert.Equal(0.4f, authored.StartAngle);
        Assert.Equal(0.5f, authored.Rx);
        Assert.Equal(0.6f, authored.Rz);
        Assert.Equal(777f, authored.Distance);
        Assert.Equal(1.25f, authored.OrbitsPerHour);
        Assert.Equal(2.5f, authored.PhasePerHour);
        Assert.True(authored.EmitsLight);
        Assert.Equal("LightIntensity.Custom", authored.LightIntensitySpline);
        Assert.Equal("SkyIntensity.Custom", authored.SkyIntensitySpline);
        Assert.Equal(0.75f, authored.LightRadius);
        Assert.True(authored.ForceVisible);

        EnbRetailOrbitalDefinition defaults = scene.Orbitals[1];
        Assert.Equal(456, defaults.BaseAsset);
        Assert.Equal(1f, defaults.Scale);
        Assert.Equal(100f, defaults.Distance);
        Assert.Equal("LightIntensity.Sun", defaults.LightIntensitySpline);
        Assert.Equal("SkyIntensity.Sun", defaults.SkyIntensitySpline);
        Assert.False(defaults.EmitsLight);
        Assert.False(defaults.ForceVisible);
    }

    [Fact]
    public void Base811ExtraStringIsConsumedBeforeScalarCurveBody()
    {
        EnbRetailScalarSplineDatabase database = EnbRetailScalarSplineDatabase.ParseWsd(BuildWsdChunks(
            (0x811u, CurveRecord811(
                "Hermite.Extra_R",
                type: 4,
                extraString: "CurrentRetailMetadata",
                loop: false,
                (0f, 0f, 91f, 92f, 93f),
                (1f, 1f, 81f, 82f, 83f)))));

        Assert.True(database.TrySample("Hermite.Extra", 'R', 0.25f, out float sampled));
        Assert.Equal(0.1796875f, sampled, 6);
    }

    [Fact]
    public void BaseTypeTwoExposesItsCurrentRetailHermiteCurve()
    {
        EnbRetailScalarSplineDatabase database = EnbRetailScalarSplineDatabase.ParseWsd(BuildWsd(
            Type2CurveRecord(
                "Type2.Test_R",
                loop: false,
                referenceA: "Ref.A",
                referenceB: "Ref.B",
                (0.625f, 0.5f, 91f, 92f, 93f))));

        Assert.True(database.TrySample("Type2.Test", 'R', 0.5f, out float sampled));
        Assert.Equal(0.625f, sampled);
    }

    [Fact]
    public void RuntimeSplineTypeChangeKeepsTheEntryLikeTheCurrentRetailAssertion()
    {
        EnbRetailScalarSplineDatabase database = EnbRetailScalarSplineDatabase.ParseWsd(BuildWsd(
            CurveRecord("Runtime.Typed_R", type: 3, loop: false, (0.25f, 0.5f, 0f))));

        // 00489830 asserts on a type change and leaves the entry unchanged.
        database.ApplyRuntimeUpdate(BuildRuntimeCurveUpdate(
            "Runtime.Typed_R",
            type: 4,
            loop: false,
            (0.75f, 0.5f)));

        Assert.True(database.TrySample("Runtime.Typed", 'R', 0.5f, out float retained));
        Assert.Equal(0.25f, retained);
    }

    [Fact]
    public void RuntimeSplineRecordsFollowTheRetailCursor()
    {
        EnbRetailScalarSplineDatabase database = EnbRetailScalarSplineDatabase.CreateEmpty();
        uint a = EnbRetailScalarSplineDatabase.ComputeCurrentRetailCrc32("Cursor.A_R");
        uint b = EnbRetailScalarSplineDatabase.ComputeCurrentRetailCrc32("Cursor.B_R");
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true))
        {
            writer.Write((ushort)3);
            writer.Write(0u);                                   // 00486F60: zero CRC consumes only itself
            writer.Write(a); writer.Write((byte)3); writer.Write((byte)0); writer.Write(1u); writer.Write(0.75f); writer.Write(0.5f);
            writer.Write(b); writer.Write((byte)9);             // 00489830: unknown type advances one byte
            writer.Write((byte)0xEE); writer.Write((byte)0xEE); // ignored trailing bytes
            writer.Flush();
            database.ApplyRuntimeUpdate(stream.ToArray());
        }
        Assert.True(database.TrySample("Cursor.A", 'R', 0.5f, out float sampled));
        Assert.Equal(0.75f, sampled);
        Assert.False(database.TrySample("Cursor.B", 'R', 0.5f, out _));

        // A record that runs past the payload stops decoding; the prefix stays applied.
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true))
        {
            writer.Write((ushort)2);
            writer.Write(a); writer.Write((byte)3); writer.Write((byte)0); writer.Write(1u); writer.Write(0.25f); writer.Write(0.5f);
            writer.Write(EnbRetailScalarSplineDatabase.ComputeCurrentRetailCrc32("Cursor.C_R")); writer.Write((byte)3); writer.Write((byte)0); writer.Write(5u); writer.Write(0.5f);
            writer.Flush();
            database.ApplyRuntimeUpdate(stream.ToArray());
        }
        Assert.True(database.TrySample("Cursor.A", 'R', 0.5f, out sampled));
        Assert.Equal(0.25f, sampled);
        Assert.False(database.TrySample("Cursor.C", 'R', 0.5f, out _));
    }

    [Fact]
    public void RuntimeSplineUpdatesReplaceScalarStateCumulatively()
    {
        EnbRetailScalarSplineDatabase database = EnbRetailScalarSplineDatabase.ParseWsd(BuildWsd(
            CurveRecord("Runtime.Test_R", type: 3, loop: false, (0.1f, 0f, 0f))));

        database.ApplyRuntimeUpdate(BuildRuntimeType3Update("Runtime.Test_R", 0.75f));
        database.ApplyRuntimeUpdate(BuildRuntimeType3Update("Runtime.Other_R", 0.25f));

        Assert.True(database.TrySample("Runtime.Test", 'R', 0.5f, out float first));
        Assert.True(database.TrySample("Runtime.Other", 'R', 0.5f, out float second));
        Assert.Equal(0.75f, first);
        Assert.Equal(0.25f, second);
    }

    [Fact]
    public void RetainedSplineRevisionsPreserveLoadPrefixAndQueuedReaders()
    {
        var source=EnbRetailScalarSplineDatabase.CreateEmpty();
        EnbSceneSplineUpdateSnapshot Update(float value)=>new(BuildRuntimeType3Update("Runtime.Test_R",value).ToImmutableArray());
        var load=new EnbRetainedSceneSplineState();
        var current=new EnbRetainedSceneSplineState();
        ImmutableArray<EnbSceneSplineUpdateSnapshot> updates=[Update(.25f),Update(.75f)];
        Assert.True(load.TryResolve(source,updates,1,out var prefix,out _));
        Assert.True(current.TryResolve(source,updates,2,out var latest,out _));
        Assert.True(prefix.TrySample("Runtime.Test",'R',0,out var atLoad));
        Assert.Equal(.25f,atLoad);
        Assert.True(latest.TrySample("Runtime.Test",'R',0,out var now));
        Assert.Equal(.75f,now);
        // Snapshot publication can copy byte arrays without adding a mutation.
        Assert.True(current.TryResolve(source,[Update(.25f),Update(.75f)],2,out var same,out _));
        Assert.Same(latest,same);
        Assert.True(current.TryResolve(source,updates.Add(Update(.5f)),3,out var appended,out _));
        Assert.NotSame(latest,appended);
        Assert.True(latest.TrySample("Runtime.Test",'R',0,out now));Assert.Equal(.75f,now);
        Assert.True(appended.TrySample("Runtime.Test",'R',0,out now));Assert.Equal(.5f,now);
        // Replacing an earlier payload, rewinding, and replacing the base all invalidate.
        Assert.True(current.TryResolve(source,[Update(.125f)],1,out var replaced,out _));
        Assert.True(replaced.TrySample("Runtime.Test",'R',0,out now));Assert.Equal(.125f,now);
        Assert.True(current.TryResolve(EnbRetailScalarSplineDatabase.CreateEmpty(),[],0,out var empty,out _));
        Assert.False(empty.TrySample("Runtime.Test",'R',0,out _));
        current.Clear();
        Assert.True(current.TryResolve(source,updates,2,out var rebuilt,out _));Assert.NotSame(latest,rebuilt);
        var malformed=updates.Add(new(ImmutableArray.Create<byte>(1,0)));
        // 00486F60: a record past the payload is an undefined native read, so
        // decoding stops there and the earlier updates stay applied.
        Assert.True(current.TryResolve(source,malformed,3,out var truncated,out var diagnostic));Assert.Null(diagnostic);
        Assert.True(truncated.TrySample("Runtime.Test",'R',0,out now));Assert.Equal(.75f,now);
    }

    [Fact]
    public void WorldAndPresentationSnapshotRetainOrderedSplineMutations()
    {
        var world = new Net7WorldState();
        byte[] first = BuildRuntimeType3Update("Runtime.First_R", 0.25f);
        byte[] second = BuildRuntimeType3Update("Runtime.Second_R", 0.75f);

        world.Apply(Packet.ForOpcode(0x0038, WrapSplinePacketData(first)), 0);
        world.Apply(Packet.ForOpcode(0x0038, WrapSplinePacketData(second)), 0);

        Assert.Equal(2, world.SplineUpdates.Count);
        var snapshot = new EnbPresentationSnapshotBuilder().Build(world, assets: null, clientTime: 0);
        Assert.Equal(2, snapshot.SplineUpdates.Length);
        Assert.Equal(first, snapshot.SplineUpdates[0].Data.ToArray());
        Assert.Equal(second, snapshot.SplineUpdates[1].Data.ToArray());
    }

    [Fact]
    public void PhasePerHourDoesNotAlterFogOrbitalTransform()
    {
        EnbRetailOrbitalDefinition noPhase = Orbital(0, emitsLight: false, distance: 100f, orbitsPerHour: 0.75f);
        EnbRetailOrbitalDefinition withPhase = noPhase with { PhasePerHour = 37.5f };

        Assert.True(EnbRetailOrbitalFogEvaluator.TryBuildOrbitalWorldTransform(
            noPhase, 0.375f, new Vector3(3f, 4f, 5f), out Matrix4x4 first, out Vector3 firstDirection, out float firstElevation, out float firstT));
        Assert.True(EnbRetailOrbitalFogEvaluator.TryBuildOrbitalWorldTransform(
            withPhase, 0.375f, new Vector3(3f, 4f, 5f), out Matrix4x4 second, out Vector3 secondDirection, out float secondElevation, out float secondT));

        Assert.Equal(first, second);
        Assert.Equal(firstDirection, secondDirection);
        Assert.Equal(firstElevation, secondElevation);
        Assert.Equal(firstT, secondT);
    }

    [Fact]
    public void OrbitalTransformUsesCurrentRetailElevationCoordinate()
    {
        EnbRetailOrbitalDefinition orbital = Orbital(
            index: 0,
            emitsLight: false,
            distance: 100f,
            orbitsPerHour: 1f);

        Assert.True(EnbRetailOrbitalFogEvaluator.TryBuildOrbitalWorldTransform(
            orbital,
            timeHours: 0f,
            cameraPosition: Vector3.Zero,
            out Matrix4x4 initial,
            out Vector3 initialDirection,
            out float initialElevation,
            out float initialT));
        AssertClose(new Vector3(0f, 0f, -1f), initialDirection);
        AssertClose(new Vector3(0f, 0f, -100f), new Vector3(initial.M41, initial.M42, initial.M43));
        Assert.Equal(-MathF.PI / 2f, initialElevation, 5);
        Assert.Equal(0f, initialT, 5);

        Assert.True(EnbRetailOrbitalFogEvaluator.TryBuildOrbitalWorldTransform(
            orbital,
            timeHours: 0.25f,
            cameraPosition: Vector3.Zero,
            out Matrix4x4 quarter,
            out Vector3 quarterDirection,
            out _,
            out float quarterT));
        AssertClose(new Vector3(1f, 0f, 0f), quarterDirection, 1e-5f);
        AssertClose(new Vector3(100f, 0f, 0f), new Vector3(quarter.M41, quarter.M42, quarter.M43), 1e-4f);
        Assert.Equal(0.5f, quarterT, 5);
    }

    [Fact]
    public void OrdinarySceneDayNightPhaseMatchesCurrentRetailSignedAngleGeometry()
    {
        Vector3 planet = Vector3.Zero;
        Vector3 camera = Vector3.UnitX;

        Assert.True(EnbRetailOrbitalFogEvaluator.TryComputeOrdinarySceneDayNightPhase(
            planet, camera, Vector3.UnitX, out float aligned));
        Assert.True(EnbRetailOrbitalFogEvaluator.TryComputeOrdinarySceneDayNightPhase(
            planet, camera, Vector3.UnitY, out float positiveQuarter));
        Assert.True(EnbRetailOrbitalFogEvaluator.TryComputeOrdinarySceneDayNightPhase(
            planet, camera, -Vector3.UnitX, out float opposite));
        Assert.True(EnbRetailOrbitalFogEvaluator.TryComputeOrdinarySceneDayNightPhase(
            planet, camera, -Vector3.UnitY, out float negativeQuarter));

        Assert.Equal(1f, aligned, 6);
        Assert.Equal(0.75f, positiveQuarter, 6);
        Assert.Equal(0.5f, opposite, 6);
        Assert.Equal(0.25f, negativeQuarter, 6);
    }


    [Fact]
    public void OrbitalSkyColorAndIntensityDriveSceneFogAndOtherOrbitalsOcclude()
    {
        EnbRetailScalarSplineDatabase splines = EnbRetailScalarSplineDatabase.ParseWsd(BuildWsd(
            CurveRecord("SkyColor.Earth_R", 3, false, (0.5f, 0f, 0f)),
            CurveRecord("SkyColor.Earth_G", 3, false, (0.25f, 0f, 0f)),
            CurveRecord("SkyColor.Earth_B", 3, false, (1f, 0f, 0f)),
            CurveRecord("SkyIntensity.Sun_R", 3, false, (1f, 0f, 0f)),
            CurveRecord("SkyIntensity.Sun_G", 3, false, (0.5f, 0f, 0f)),
            CurveRecord("SkyIntensity.Sun_B", 3, false, (0.25f, 0f, 0f))));

        var clearScene = new EnbRetailOrbitalSceneDefinition(
            "LightColor.Earth",
            "SkyColor.Earth",
            "SkyEmissive.Default",
            new[] { Orbital(0, emitsLight: true, distance: 100f) });

        Assert.True(EnbRetailOrbitalFogEvaluator.TryEvaluate(
            Environment(5, 100f, 300f),
            clientTimeMilliseconds: 0,
            cameraPosition: Vector3.Zero,
            clearScene,
            splines,
            Vector3.Zero,
            ResolveClearSphere,
            out EnbRetailOrbitalFogEvaluation clear,
            out string? clearDiagnostic), clearDiagnostic);
        Assert.True(clear.SceneFog.Enabled);
        Assert.Equal(0x7F_1F_3Fu, clear.SceneFog.SceneColorRgb24);
        Assert.Equal(1, clear.EmittingOrbitalCount);
        Assert.Equal(0, clear.OccludedSampleCount);

        var occludedScene = clearScene with
        {
            Orbitals = new[]
            {
                Orbital(0, emitsLight: true, distance: 100f),
                Orbital(1, emitsLight: false, distance: 50f),
            },
        };
        Assert.True(EnbRetailOrbitalFogEvaluator.TryEvaluate(
            Environment(5, 100f, 300f),
            0,
            Vector3.Zero,
            occludedScene,
            splines,
            Vector3.Zero,
            ResolveOccludedSphere,
            out EnbRetailOrbitalFogEvaluation occluded,
            out string? occludedDiagnostic), occludedDiagnostic);
        Assert.Equal(0u, occluded.SceneFog.SceneColorRgb24);
        Assert.Equal(16, occluded.OccludedSampleCount);
    }

    [Fact]
    public void PlanetTerrainRetainsSkyAmbientWithoutAnActiveOrbitalLight()
    {
        var scene = new EnbRetailOrbitalSceneDefinition("unused", "unused", "unused", []);
        var splines = EnbRetailScalarSplineDatabase.ParseWsd(BuildWsd());
        var skyAmbient = new Vector3(.03f, .2f, .03f);
        Assert.True(EnbRetailOrbitalFogEvaluator.TryEvaluate(Environment(1, 100f, 300f),
            0, Vector3.Zero, scene, splines, skyAmbient, ResolveClearSphere,
            out var evaluation, out var diagnostic), diagnostic);
        Assert.Equal(Vector3.Zero, evaluation.FogRgb);
        Assert.NotNull(evaluation.TerrainLight);
        Assert.False(evaluation.TerrainLight.Enabled);
        Assert.Equal(skyAmbient, evaluation.TerrainLight.Ambient);
    }

    private static bool ResolveClearSphere(
        EnbRetailOrbitalDefinition orbital,
        Matrix4x4 world,
        out EnbW3dWorldBounds sphere,
        out string? diagnostic)
    {
        sphere = new EnbW3dWorldBounds(new Vector3(world.M41, world.M42, world.M43), 10f);
        diagnostic = null;
        return true;
    }

    private static bool ResolveOccludedSphere(
        EnbRetailOrbitalDefinition orbital,
        Matrix4x4 world,
        out EnbW3dWorldBounds sphere,
        out string? diagnostic)
    {
        sphere = orbital.Index == 0
            ? new EnbW3dWorldBounds(new Vector3(0f, 0f, -100f), 10f)
            : new EnbW3dWorldBounds(new Vector3(0f, 0f, -50f), 40f);
        diagnostic = null;
        return true;
    }

    private static EnbSceneEnvironmentSnapshot Environment(int? mode, float fogNear, float fogFar)
        => new(
            BaseAsset: 0,
            SwapBackdrop: false,
            FinalRawValue: 7,
            RawServerParameters: ImmutableArray<byte>.Empty,
            BaseAssetReference: null,
            FogNear: fogNear,
            FogFar: fogFar,
            LightBackdrop: false,
            FogBackdrop: false,
            BackdropFogNear: 0f,
            BackdropFogFar: 0f,
            PresentationMode: mode,
            SplineUpdateCountAtLoad: 0);

    private static EnbRetailOrbitalDefinition Orbital(
        int index,
        bool emitsLight,
        float distance,
        float orbitsPerHour = 0f)
        => new(
            Index: index,
            BaseAsset: 1,
            Scale: 1f,
            HsvH: 0f,
            HsvS: 0f,
            HsvV: 0f,
            StartAngle: 0f,
            Rx: 0f,
            Rz: 0f,
            Distance: distance,
            OrbitsPerHour: orbitsPerHour,
            PhasePerHour: 0f,
            EmitsLight: emitsLight,
            LightIntensitySpline: "LightIntensity.Sun",
            SkyIntensitySpline: "SkyIntensity.Sun",
            LightRadius: 0.1f,
            ForceVisible: false);

    private static byte[] WrapSplinePacketData(byte[] data)
    {
        byte[] payload = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), checked((uint)data.Length));
        data.CopyTo(payload, 4);
        return payload;
    }

    private static byte[] BuildRuntimeType3Update(string name, float value)
        => BuildRuntimeCurveUpdate(name, type: 3, loop: false, (value, 0.5f));

    private static byte[] BuildRuntimeCurveUpdate(
        string name,
        byte type,
        bool loop,
        params (float Value, float Time)[] keys)
    {
        if (type is not (3 or 4))
            throw new ArgumentOutOfRangeException(nameof(type));

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);
        writer.Write((ushort)1);
        writer.Write(EnbRetailScalarSplineDatabase.ComputeCurrentRetailCrc32(name));
        writer.Write(type);
        writer.Write((byte)(loop ? 1 : 0));
        writer.Write((uint)keys.Length);
        foreach ((float value, float time) in keys)
        {
            writer.Write(value);
            writer.Write(time);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildWsd(params byte[][] records)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);

        WriteWsdHeaderChunk(writer, records.Length);
        WriteChunk(writer, 0x808u, new byte[4], container: false);
        foreach (byte[] record in records)
            WriteChunk(writer, 0x810u, record, container: false);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] CurveRecord(
        string name,
        byte type,
        bool loop,
        params (float Value, float Time, float Extra0, float Extra1, float Extra2)[] keys)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);
        byte[] nameBytes = [.. Encoding.Latin1.GetBytes(name), 0];
        writer.Write((uint)type);
        writer.Write((uint)nameBytes.Length);
        writer.Write(new byte[0x24 - 8]);
        writer.Write(nameBytes);
        writer.Write((byte)(loop ? 1 : 0));
        writer.Write((uint)keys.Length);
        foreach ((float value, float time, float extra0, float extra1, float extra2) in keys)
        {
            writer.Write(value);
            writer.Write(time);
            writer.Write(extra0);
            if (type is 2 or 4)
            {
                writer.Write(extra1);
                writer.Write(extra2);
            }
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] CurveRecord(
        string name,
        byte type,
        bool loop,
        params (float Value, float Time, float Extra0)[] keys)
        => CurveRecord(name, type, loop, keys.Select(static key => (key.Value, key.Time, key.Extra0, 0f, 0f)).ToArray());

    private static byte[] BuildWsdChunks(params (uint ChunkType, byte[] Record)[] records)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);

        WriteWsdHeaderChunk(writer, records.Length);
        WriteChunk(writer, 0x808u, new byte[4], container: false);
        foreach ((uint chunkType, byte[] record) in records)
            WriteChunk(writer, chunkType, record, container: false);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteWsdHeaderChunk(BinaryWriter writer, int recordCount)
    {
        using var headerStream = new MemoryStream();
        using var header = new BinaryWriter(headerStream, Encoding.Latin1, leaveOpen: true);
        header.Write(0x100u);
        header.Write((uint)recordCount);
        header.Write(0u);
        header.Write(0u);
        header.Flush();
        WriteChunk(writer, 0x800u, headerStream.ToArray(), container: true);
    }

    private static byte[] CurveRecord811(
        string name,
        byte type,
        string extraString,
        bool loop,
        params (float Value, float Time, float Extra0, float Extra1, float Extra2)[] keys)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);
        byte[] nameBytes = [.. Encoding.Latin1.GetBytes(name), 0];
        byte[] extraBytes = [.. Encoding.Latin1.GetBytes(extraString), 0];
        writer.Write((uint)type);
        writer.Write((uint)nameBytes.Length);
        writer.Write(new byte[0x24 - 8]);
        writer.Write(nameBytes);
        writer.Write((uint)extraBytes.Length);
        writer.Write(extraBytes);
        writer.Write((byte)(loop ? 1 : 0));
        writer.Write((uint)keys.Length);
        foreach ((float value, float time, float extra0, float extra1, float extra2) in keys)
        {
            writer.Write(value);
            writer.Write(time);
            writer.Write(extra0);
            if (type is 2 or 4)
            {
                writer.Write(extra1);
                writer.Write(extra2);
            }
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] Type2CurveRecord(
        string name,
        bool loop,
        string referenceA,
        string referenceB,
        params (float Value, float Time, float Extra0, float Extra1, float Extra2)[] keys)
    {
        byte[] curve = CurveRecord(name, type: 2, loop, keys);
        using var stream = new MemoryStream();
        stream.Write(curve);
        using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);
        WriteLengthPrefixedLatin1(writer, referenceA);
        WriteLengthPrefixedLatin1(writer, referenceB);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteLengthPrefixedLatin1(BinaryWriter writer, string value)
    {
        byte[] bytes = [.. Encoding.Latin1.GetBytes(value), 0];
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteChunk(BinaryWriter writer, uint type, byte[] payload, bool container)
    {
        writer.Write(type);
        writer.Write((uint)payload.Length | (container ? 0x8000_0000u : 0u));
        writer.Write(payload);
    }

    private static void WriteF32(byte[] bytes, int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));

    private static void AssertClose(Vector3 expected, Vector3 actual, float epsilon = 1e-6f)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, epsilon);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, epsilon);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, epsilon);
    }
}
