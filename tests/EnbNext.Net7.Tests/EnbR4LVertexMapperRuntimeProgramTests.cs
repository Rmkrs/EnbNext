using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4LVertexMapperRuntimeProgramTests
{
    [Theory]
    [InlineData(".03f", .03f)]
    [InlineData(".01f", .01f)]
    [InlineData("3e-2f", .03f)]
    [InlineData("3%", .03f)]
    public void StationAuthoredRotationSpeedUsesRetailNumericPrefix(string authored, float speed)
    {
        var program = Compile(Mapper(EnbW3dMapperTypeSelector.Mode08,
            $"Speed={authored}\r\nUCenter=0f\r\nVCenter=0f\r\nUScale=1.5")).Mapper0Program;
        Assert.InRange(MathF.Abs(program.Rotation!.Value.RadiansPerMillisecond - speed * MathF.Tau / 1000), 0, 1e-9f);
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(program, 1000, out var state, out var diagnostic), diagnostic);
        var actual = state.Evaluate(2000);
        var plain = Compile(Mapper(EnbW3dMapperTypeSelector.Mode08,
            $"Speed={speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}\r\nUCenter=0\r\nVCenter=0\r\nUScale=1.5")).Mapper0Program;
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(plain, 1000, out var reference, out diagnostic), diagnostic);
        Assert.Equal(reference.Evaluate(2000), actual);
    }

    [Fact]
    public void R4LEveryCurrentRetailSelectorCompilesToAFiniteRuntimeProgram()
    {
        (EnbW3dMapperTypeSelector Selector, EnbW3dMapperRuntimeProgramKind Kind, EnbW3dMapperCoordinateSource Source)[] expected =
        [
            (EnbW3dMapperTypeSelector.Mode01, EnbW3dMapperRuntimeProgramKind.FixedHalfBias, EnbW3dMapperCoordinateSource.CameraSpaceReflectionVector),
            (EnbW3dMapperTypeSelector.Mode02, EnbW3dMapperRuntimeProgramKind.FixedHalfBias, EnbW3dMapperCoordinateSource.CameraSpaceNormal),
            (EnbW3dMapperTypeSelector.Mode03, EnbW3dMapperRuntimeProgramKind.ProjectedLinear, EnbW3dMapperCoordinateSource.CameraSpacePosition),
            (EnbW3dMapperTypeSelector.Mode04, EnbW3dMapperRuntimeProgramKind.Linear, EnbW3dMapperCoordinateSource.PassThrough),
            (EnbW3dMapperTypeSelector.Mode06, EnbW3dMapperRuntimeProgramKind.StaticScale, EnbW3dMapperCoordinateSource.PassThrough),
            (EnbW3dMapperTypeSelector.Mode07, EnbW3dMapperRuntimeProgramKind.GridAtlas, EnbW3dMapperCoordinateSource.PassThrough),
            (EnbW3dMapperTypeSelector.Mode08, EnbW3dMapperRuntimeProgramKind.CenteredRotation, EnbW3dMapperCoordinateSource.PassThrough),
            (EnbW3dMapperTypeSelector.Mode09, EnbW3dMapperRuntimeProgramKind.SinusoidalOffset, EnbW3dMapperCoordinateSource.PassThrough),
            (EnbW3dMapperTypeSelector.Mode0A, EnbW3dMapperRuntimeProgramKind.SteppedOffset, EnbW3dMapperCoordinateSource.PassThrough),
            (EnbW3dMapperTypeSelector.Mode0B, EnbW3dMapperRuntimeProgramKind.PingPongOffset, EnbW3dMapperCoordinateSource.PassThrough),
            (EnbW3dMapperTypeSelector.Mode0C, EnbW3dMapperRuntimeProgramKind.AxisProjection, EnbW3dMapperCoordinateSource.CameraSpaceNormal),
            (EnbW3dMapperTypeSelector.Mode0D, EnbW3dMapperRuntimeProgramKind.AxisProjection, EnbW3dMapperCoordinateSource.CameraSpaceReflectionVector),
            (EnbW3dMapperTypeSelector.Mode0E, EnbW3dMapperRuntimeProgramKind.GridAtlas, EnbW3dMapperCoordinateSource.CameraSpaceNormal),
            (EnbW3dMapperTypeSelector.Mode0F, EnbW3dMapperRuntimeProgramKind.GridAtlas, EnbW3dMapperCoordinateSource.CameraSpaceReflectionVector),
            (EnbW3dMapperTypeSelector.Mode10, EnbW3dMapperRuntimeProgramKind.Randomized, EnbW3dMapperCoordinateSource.PassThrough),
            (EnbW3dMapperTypeSelector.Mode11, EnbW3dMapperRuntimeProgramKind.VerticalEnvironmentScroll, EnbW3dMapperCoordinateSource.CameraSpaceNormal),
            (EnbW3dMapperTypeSelector.Mode12, EnbW3dMapperRuntimeProgramKind.BumpEnvironment, EnbW3dMapperCoordinateSource.PassThrough),
            (EnbW3dMapperTypeSelector.Mode13, EnbW3dMapperRuntimeProgramKind.GridAtlasAxisProjection, EnbW3dMapperCoordinateSource.CameraSpaceNormal),
            (EnbW3dMapperTypeSelector.Mode14, EnbW3dMapperRuntimeProgramKind.GridAtlasAxisProjection, EnbW3dMapperCoordinateSource.CameraSpaceReflectionVector),
        ];

        foreach ((EnbW3dMapperTypeSelector selector, EnbW3dMapperRuntimeProgramKind kind, EnbW3dMapperCoordinateSource source) in expected)
        {
            EnbW3dCompiledMaterialSemantics compiled = Compile(Mapper(selector, string.Empty));
            Assert.Equal(kind, compiled.Mapper0Program.Kind);
            Assert.Equal(source, compiled.Mapper0Program.CoordinateSource);
            Assert.NotEqual(EnbW3dMapperRuntimeProgramKind.Disabled, compiled.Mapper0Program.Kind);
        }
    }

    [Fact]
    public void R4LProjectedLinearAndGeneratedEnvironmentProgramsCarryExactCoordinateSourceBoundaries()
    {
        EnbW3dMapperRuntimeProgram projected = Compile(Mapper(
            EnbW3dMapperTypeSelector.Mode03,
            "UScale=2\r\nVScale=3\r\nUPerSec=4\r\nVPerSec=-5\r\nUOffset=.25\r\nVOffset=-.5\r\nClampFix=Y",
            uScale: 2,
            vScale: 3,
            uPerSecond: 4,
            vPerSecond: -5,
            uOffset: .25f,
            vOffset: -.5f,
            clampFix: true)).Mapper0Program;

        Assert.Equal(EnbW3dMapperTransformShape.ProjectedCount3, projected.TransformShape);
        Assert.Equal(EnbW3dMapperCoordinateSource.CameraSpacePosition, projected.CoordinateSource);
        Assert.True(projected.RequiresViewTransform);
        Assert.True(projected.UsesElapsedTime);
        Assert.Equal(new Vector2(2, 3), projected.BaseUniforms.Scale);
        AssertVector(new Vector2(-.004f, .005f), projected.BaseUniforms.ScrollPerMillisecond);
        Assert.Equal(new Vector2(.25f, -.5f), projected.BaseUniforms.Offset);
        Assert.True(projected.BaseUniforms.ClampFix);

        EnbW3dMapperRuntimeProgram axis = Compile(Mapper(
            EnbW3dMapperTypeSelector.Mode0D,
            "Axis=y")).Mapper0Program;
        Assert.Equal(EnbW3dMapperProjectionAxis.Y, axis.Axis);
        Assert.Equal(EnbW3dMapperCoordinateSource.CameraSpaceReflectionVector, axis.CoordinateSource);
        Assert.Equal(EnbW3dMapperTransformShape.Count2, axis.TransformShape);
        Assert.True(axis.RequiresViewTransform);
    }

    [Fact]
    public void R4LAtlasRotationSineAndStepConstructorsPreserveTheirProvenArgumentsAndUnits()
    {
        EnbW3dMapperRuntimeProgram atlas = Compile(Mapper(
            EnbW3dMapperTypeSelector.Mode07,
            "FPS=-2\r\nLog2Width=3\r\nLast=17\r\nOffset=4")).Mapper0Program;
        Assert.True(atlas.Atlas.HasValue);
        Assert.Equal(new EnbW3dMapperAtlasParameters(-2, 3, 17, 4), atlas.Atlas.Value);

        EnbW3dMapperRuntimeProgram rotation = Compile(Mapper(
            EnbW3dMapperTypeSelector.Mode08,
            "Speed=25%\r\nUCenter=.2\r\nVCenter=.7",
            uScale: 2,
            vScale: 4)).Mapper0Program;
        Assert.True(rotation.Rotation.HasValue);
        EnbW3dMapperRotationParameters rotationArgs = rotation.Rotation.Value;
        Assert.Equal(new Vector2(2, 4), rotationArgs.Scale);
        Assert.InRange(MathF.Abs(rotationArgs.RadiansPerMillisecond - (.25f * MathF.PI * 2f / 1000f)), 0f, 1e-9f);
        Assert.Equal(new Vector2(.2f, .7f), rotationArgs.Center);

        EnbW3dMapperRuntimeProgram sine = Compile(Mapper(
            EnbW3dMapperTypeSelector.Mode09,
            "UAmp=.5\r\nUFreq=2\r\nUPhase=.25\r\nVAmp=.75\r\nVFreq=3\r\nVPhase=.5",
            uScale: 6,
            vScale: 8)).Mapper0Program;
        Assert.True(sine.Sine.HasValue);
        EnbW3dMapperSineParameters sineArgs = sine.Sine.Value;
        Assert.Equal(new Vector2(6, 8), sineArgs.Scale);
        Assert.Equal(.5f, sineArgs.UAmplitude);
        Assert.Equal(2f, sineArgs.UFrequency);
        Assert.Equal(.25f, sineArgs.UPhase);
        Assert.Equal(.75f, sineArgs.VAmplitude);
        Assert.Equal(3f, sineArgs.VFrequency);
        Assert.Equal(.5f, sineArgs.VPhase);

        EnbW3dMapperRuntimeProgram stepped = Compile(Mapper(
            EnbW3dMapperTypeSelector.Mode0A,
            "UStep=.125\r\nVStep=-.25\r\nSPS=4\r\nClampFix=T",
            uScale: 3,
            vScale: 5)).Mapper0Program;
        Assert.True(stepped.Step.HasValue);
        EnbW3dMapperStepParameters stepArgs = stepped.Step.Value;
        Assert.Equal(new Vector2(.125f, -.25f), stepArgs.Step);
        Assert.Equal(.004f, stepArgs.StepsPerMillisecond, 6);
        Assert.True(stepArgs.ClampFix);
    }

    [Fact]
    public void R4LPingPongRandomEnvironmentAndBumpProgramsKeepDistinctRetailTimeConventions()
    {
        EnbW3dMapperRuntimeProgram pingPong = Compile(Mapper(
            EnbW3dMapperTypeSelector.Mode0B,
            "Period=-2.5",
            uScale: 2,
            vScale: 3,
            uPerSecond: 4,
            vPerSecond: -6)).Mapper0Program;
        Assert.True(pingPong.PingPong.HasValue);
        EnbW3dMapperPingPongParameters pingPongArgs = pingPong.PingPong.Value;
        AssertVector(new Vector2(.004f, -.006f), pingPongArgs.PerMillisecond);
        Assert.Equal(2500f, pingPongArgs.PeriodMilliseconds, 3);

        EnbW3dMapperRuntimeProgram randomized = Compile(Mapper(
            EnbW3dMapperTypeSelector.Mode10,
            "FPS=2",
            uScale: 7,
            vScale: 9,
            uPerSecond: 5,
            vPerSecond: -10)).Mapper0Program;
        Assert.True(randomized.Randomized.HasValue);
        EnbW3dMapperRandomizedParameters randomArgs = randomized.Randomized.Value;
        Assert.True(randomized.RequiresRandomSource);
        Assert.Equal(.002f, randomArgs.EventsPerMillisecond, 6);
        AssertVector(new Vector2(.005f, -.01f), randomArgs.PerMillisecond);

        EnbW3dCompiledMaterialSemantics reflectedEnvironment = Compile(Mapper(
            EnbW3dMapperTypeSelector.Mode11,
            "VStart=.33\r\nUseReflect=Yes",
            vPerSecond: 8));
        EnbW3dMapperRuntimeProgram environment = reflectedEnvironment.Mapper0Program;
        Assert.True(environment.EnvironmentScroll.HasValue);
        EnbW3dMapperEnvironmentScrollParameters environmentArgs = environment.EnvironmentScroll.Value;
        Assert.Equal(EnbW3dMapperCoordinateSource.CameraSpaceReflectionVector, environment.CoordinateSource);
        Assert.Equal(8f, environmentArgs.VPerSecond);
        Assert.Equal(.33f, environmentArgs.VStart, 6);
        Assert.True(environmentArgs.UseReflection);

        EnbW3dCompiledMaterialSemantics normalEnvironment = Compile(Mapper(
            EnbW3dMapperTypeSelector.Mode11,
            "UseReflect=No",
            vPerSecond: 8));
        Assert.Equal(EnbW3dMapperCoordinateSource.CameraSpaceNormal, normalEnvironment.Mapper0Program.CoordinateSource);
        Assert.NotEqual(normalEnvironment.PermutationKey.Mapper0, reflectedEnvironment.PermutationKey.Mapper0);

        EnbW3dMapperRuntimeProgram bump = Compile(Mapper(
            EnbW3dMapperTypeSelector.Mode12,
            "BumpRotation=.25\r\nBumpScale=1.5",
            uScale: 2,
            vScale: 3,
            uPerSecond: 1,
            vPerSecond: 2)).Mapper0Program;
        Assert.True(bump.Bump.HasValue);
        EnbW3dMapperBumpParameters bumpArgs = bump.Bump.Value;
        Assert.True(bump.ConfiguresBumpEnvironment);
        Assert.InRange(MathF.Abs(bumpArgs.RadiansPerSecond - (MathF.PI * .5f)), 0f, 1e-6f);
        Assert.Equal(1.5f, bumpArgs.Scale, 6);
        AssertVector(new Vector2(-.001f, -.002f), bump.BaseUniforms.ScrollPerMillisecond);
    }

    [Fact]
    public void R4LMissingArgumentChunksStillProduceConstructorDefaultsAndUnsupportedSelectorsFailClosed()
    {
        EnbW3dMapperRuntimeProgram rotation = Compile(Mapper(
            EnbW3dMapperTypeSelector.Mode08,
            string.Empty)).Mapper0Program;
        Assert.True(rotation.Rotation.HasValue);
        EnbW3dMapperRotationParameters rotationArgs = rotation.Rotation.Value;
        Assert.Equal(Vector2.One, rotationArgs.Scale);
        Assert.Equal(Vector2.Zero, rotationArgs.Center);
        Assert.InRange(MathF.Abs(rotationArgs.RadiansPerMillisecond - (.1f * MathF.PI * 2f / 1000f)), 0f, 1e-9f);

        EnbW3dMapperRuntimeProgram atlasAxis = Compile(Mapper(
            EnbW3dMapperTypeSelector.Mode14,
            string.Empty)).Mapper0Program;
        Assert.True(atlasAxis.Atlas.HasValue);
        Assert.Equal(new EnbW3dMapperAtlasParameters(1, 1, 0, 0), atlasAxis.Atlas.Value);
        Assert.Equal(EnbW3dMapperProjectionAxis.Z, atlasAxis.Axis);

        Assert.False(EnbW3dMapperRuntimeProgramCompiler.TryCompile(
            Mapper(EnbW3dMapperTypeSelector.Reserved05, string.Empty),
            out _,
            out string? diagnostic));
        Assert.Contains("0x05", diagnostic ?? string.Empty, StringComparison.Ordinal);
    }

    private static EnbW3dCompiledMaterialSemantics Compile(EnbW3dRenderMapper mapper)
    {
        Assert.True(RetailTestShims.TryCompileMaterial(
            Shader(),
            Material(mapper),
            1,
            out EnbW3dCompiledMaterialSemantics compiled,
            out string? diagnostic), diagnostic);
        return compiled;
    }

    private static EnbW3dRenderMapper Mapper(
        EnbW3dMapperTypeSelector selector,
        string raw,
        float uScale = 1,
        float vScale = 1,
        float uPerSecond = 0,
        float vPerSecond = 0,
        float uOffset = 0,
        float vOffset = 0,
        bool clampFix = false)
        => new(selector, raw, uScale, vScale, uPerSecond, vPerSecond, uOffset, vOffset, clampFix);

    private static EnbW3dRenderVertexMaterial Material(EnbW3dRenderMapper mapper)
        => new(
            Ordinal: 0,
            Name: "R4L",
            Attributes: 0,
            Ambient: new EnbRenderPackedRgb8(255, 255, 255, 0),
            Diffuse: new EnbRenderPackedRgb8(255, 255, 255, 0),
            Specular: new EnbRenderPackedRgb8(0, 0, 0, 0),
            Emissive: new EnbRenderPackedRgb8(0, 0, 0, 0),
            Power: 1.0f,
            Opacity: 1.0f,
            UnknownFloat: 0.0f,
            Mapper0: mapper,
            Mapper1: null);

    private static EnbW3dRenderShaderRecord Shader()
    {
        byte[] raw =
        [
            0x03, 0x01, 0x00, 0x00,
            0x02, 0x01, 0x00, 0x01,
            0x01, 0x02, 0x00, 0x02,
            0x00, 0x02, 0x00, 0x02,
        ];

        return new EnbW3dRenderShaderRecord(
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12, 4)));
    }

    private static void AssertVector(Vector2 expected, Vector2 actual)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, 1e-6f);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, 1e-6f);
    }
}
