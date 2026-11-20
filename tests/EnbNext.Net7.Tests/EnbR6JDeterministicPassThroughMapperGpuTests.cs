using System.Numerics;
using EnbNext.Rendering;
using EnbNext.Rendering.OpenGL;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6JDeterministicPassThroughMapperGpuTests
{

    [Fact]
    public void SharedRenderObjectMapperEpochReproducesTheSameAnimatedUvState()
    {
        EnbW3dMapperRuntimeProgram linear = Compile(
            EnbW3dMapperTypeSelector.Mode04,
            raw: string.Empty,
            uPerSecond: 1f,
            vPerSecond: -0.5f);

        Assert.True(EnbW3dMapperRuntimeState.TryCreate(
            linear,
            1_000u,
            out EnbW3dMapperRuntimeState mainViewState,
            out string? diagnostic), diagnostic);
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(
            linear,
            mainViewState.InitialMilliseconds,
            out EnbW3dMapperRuntimeState scannerState,
            out diagnostic), diagnostic);
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(
            linear,
            1_200u,
            out EnbW3dMapperRuntimeState restartedScannerState,
            out diagnostic), diagnostic);

        Assert.Equal(1_000u, mainViewState.InitialMilliseconds);
        Assert.Equal(mainViewState.InitialMilliseconds, scannerState.InitialMilliseconds);
        AssertVector2(
            mainViewState.Evaluate(1_250u).TextureOffset,
            scannerState.Evaluate(1_250u).TextureOffset);
        Assert.NotEqual(
            scannerState.Evaluate(1_300u).TextureOffset,
            restartedScannerState.Evaluate(1_300u).TextureOffset);
    }

    [Fact]
    public void LinearAndStaticScalePreserveCurrentRetailPassThroughCount2Rules()
    {
        EnbW3dMapperRuntimeProgram linear = Compile(
            EnbW3dMapperTypeSelector.Mode04,
            raw: string.Empty,
            uScale: 2f,
            vScale: 3f,
            uPerSecond: 1f,
            vPerSecond: -2f,
            uOffset: 0.25f,
            vOffset: -0.25f);
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(linear, 1000u, out EnbW3dMapperRuntimeState linearState, out string? diagnostic), diagnostic);

        EnbW3dMapperRuntimeEvaluation linearEvaluation = linearState.Evaluate(1250u);
        AssertVector2(new Vector2(2f, 3f), linearEvaluation.TextureScale);
        AssertVector2(Vector2.Zero, linearEvaluation.TextureCross);
        // The native PC24 quarter-second product is exactly -.25 before
        // adding the authored U offset (linear-production control3).
        AssertVector2(new Vector2(0f, 0.25f), linearEvaluation.TextureOffset);

        EnbW3dMapperRuntimeProgram scale = Compile(
            EnbW3dMapperTypeSelector.Mode06,
            raw: string.Empty,
            uScale: 4f,
            vScale: 5f,
            uPerSecond: 999f,
            vPerSecond: -999f,
            uOffset: 0.75f,
            vOffset: 0.25f);
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(scale, 1000u, out EnbW3dMapperRuntimeState scaleState, out diagnostic), diagnostic);
        EnbW3dMapperRuntimeEvaluation scaleEvaluation = scaleState.Evaluate(uint.MaxValue);
        AssertVector2(new Vector2(4f, 5f), scaleEvaluation.TextureScale);
        AssertVector2(Vector2.Zero, scaleEvaluation.TextureCross);
        AssertVector2(Vector2.Zero, scaleEvaluation.TextureOffset);
    }

    [Fact]
    public void GridAtlasUsesUnsignedTickFrameDirectionOffsetAndResetState()
    {
        EnbW3dMapperRuntimeProgram atlas = Compile(
            EnbW3dMapperTypeSelector.Mode07,
            "FPS=4\r\nLog2Width=2\r\nLast=0\r\nOffset=1");
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(atlas, 1000u, out EnbW3dMapperRuntimeState state, out string? diagnostic), diagnostic);

        AssertVector2(new Vector2(0.5f, 0f), state.Evaluate(1250u).TextureOffset);
        AssertVector2(new Vector2(0f, 0.25f), state.Evaluate(1750u).TextureOffset);

        state.Reset(2000u);
        AssertVector2(new Vector2(0.25f, 0f), state.Evaluate(2000u).TextureOffset);

        EnbW3dMapperRuntimeProgram reverse = Compile(
            EnbW3dMapperTypeSelector.Mode07,
            "FPS=-4\r\nLog2Width=2\r\nLast=0\r\nOffset=1");
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(reverse, 1000u, out state, out diagnostic), diagnostic);
        // Reverse construction starts at frameCount - offset - 1 = 14; one frame later => 13.
        AssertVector2(new Vector2(0.25f, 0.75f), state.Evaluate(1250u).TextureOffset);
    }

    [Fact]
    public void CenteredRotationBuildsCurrentRetailTwoByThreeAffineMatrixAndReset()
    {
        EnbW3dMapperRuntimeProgram rotation = Compile(
            EnbW3dMapperTypeSelector.Mode08,
            "Speed=.25\r\nUCenter=.25\r\nVCenter=.5",
            uScale: 2f,
            vScale: 3f);
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(rotation, 1000u, out EnbW3dMapperRuntimeState state, out string? diagnostic), diagnostic);

        EnbW3dMapperRuntimeEvaluation quarterTurn = state.Evaluate(2000u);
        AssertVector2(Vector2.Zero, quarterTurn.TextureScale, 2e-5f);
        AssertVector2(new Vector2(-2f, 3f), quarterTurn.TextureCross, 2e-5f);
        AssertVector2(new Vector2(1.5f, 0.75f), quarterTurn.TextureOffset, 2e-5f);

        state.Reset(2500u);
        EnbW3dMapperRuntimeEvaluation reset = state.Evaluate(2500u);
        AssertVector2(new Vector2(2f, 3f), reset.TextureScale);
        AssertVector2(Vector2.Zero, reset.TextureCross);
        AssertVector2(Vector2.Zero, reset.TextureOffset);
    }

    [Fact]
    public void SinusoidalOffsetUsesUnsignedMillisecondsTwoPiPerSecondAndPhaseTimesPi()
    {
        EnbW3dMapperRuntimeProgram sine = Compile(
            EnbW3dMapperTypeSelector.Mode09,
            "UAmp=.5\r\nUFreq=1\r\nUPhase=0\r\nVAmp=.25\r\nVFreq=1\r\nVPhase=0",
            uScale: 2f,
            vScale: 3f);
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(sine, 1000u, out EnbW3dMapperRuntimeState state, out string? diagnostic), diagnostic);

        EnbW3dMapperRuntimeEvaluation evaluation = state.Evaluate(1250u);
        AssertVector2(new Vector2(2f, 3f), evaluation.TextureScale);
        AssertVector2(new Vector2(0.5f, 0.25f), evaluation.TextureOffset, 2e-5f);

        state.Reset(2000u);
        AssertVector2(Vector2.Zero, state.Evaluate(2000u).TextureOffset);
    }

    [Fact]
    public void SteppedOffsetUsesTruncateTowardZeroResidualAndFloorReduction()
    {
        EnbW3dMapperRuntimeProgram step = Compile(
            EnbW3dMapperTypeSelector.Mode0A,
            "UStep=.75\r\nVStep=-.75\r\nSPS=4\r\nClampFix=F",
            uScale: 1f,
            vScale: 1f);
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(step, 1000u, out EnbW3dMapperRuntimeState state, out string? diagnostic), diagnostic);

        AssertVector2(new Vector2(0.75f, 0.25f), state.Evaluate(1250u).TextureOffset);
        AssertVector2(new Vector2(0.75f, 0.25f), state.Evaluate(1375u).TextureOffset);
        AssertVector2(new Vector2(0.5f, 0.5f), state.Evaluate(1500u).TextureOffset);

        state.Reset(2000u);
        AssertVector2(Vector2.Zero, state.Evaluate(2000u).TextureOffset);
    }

    [Fact]
    public void PingPongUsesFloatElapsedPeriodCyclesAndStrictMidpointTriangle()
    {
        EnbW3dMapperRuntimeProgram pingPong = Compile(
            EnbW3dMapperTypeSelector.Mode0B,
            "Period=2",
            uScale: 2f,
            vScale: 3f,
            uPerSecond: 1f,
            vPerSecond: -2f);
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(pingPong, 1000u, out EnbW3dMapperRuntimeState state, out string? diagnostic), diagnostic);

        AssertVector2(new Vector2(0.5f, -1f), state.Evaluate(1500u).TextureOffset);
        AssertVector2(new Vector2(0.5f, -1f), state.Evaluate(2500u).TextureOffset);
        AssertVector2(Vector2.Zero, state.Evaluate(3000u).TextureOffset);
    }

    [Fact]
    public void RandomizedProgramRemainsFailClosedAfterDeterministicGeneratedSliceMovesForward()
    {
        EnbW3dMapperRuntimeProgram randomized = Compile(EnbW3dMapperTypeSelector.Mode10, "FPS=2");
        Assert.False(EnbW3dMapperRuntimeState.TryCreate(randomized, 0u, out _, out string? diagnostic));
        Assert.Contains("random", diagnostic ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gles3ShaderCarriesTwoIndependentCount2AffineTransforms()
    {
        (string vertex, _) = EnbOpenGlStage0TextureBackend.CreateShaderSources(
            new GlVersion(GlProfileType.OpenGLES, 3, 0));

        Assert.Contains("uniform vec4 uMapper0ScaleOffset", vertex, StringComparison.Ordinal);
        Assert.Contains("uniform vec4 uMapper0Cross", vertex, StringComparison.Ordinal);
        Assert.Contains("uniform vec4 uMapper1ScaleOffset", vertex, StringComparison.Ordinal);
        Assert.Contains("uniform vec4 uMapper1Cross", vertex, StringComparison.Ordinal);
        Assert.Contains("vec2 affineMapper", vertex, StringComparison.Ordinal);
        Assert.Contains("source.y*cross.x", vertex, StringComparison.Ordinal);
        Assert.Contains("applyMapper(uMapper0Kind", vertex, StringComparison.Ordinal);
        Assert.Contains("applyMapper(uMapper1Kind", vertex, StringComparison.Ordinal);
    }

    private static EnbW3dMapperRuntimeProgram Compile(
        EnbW3dMapperTypeSelector selector,
        string raw,
        float uScale = 1f,
        float vScale = 1f,
        float uPerSecond = 0f,
        float vPerSecond = 0f,
        float uOffset = 0f,
        float vOffset = 0f,
        bool clampFix = false)
    {
        var mapper = new EnbW3dRenderMapper(selector, raw, uScale, vScale, uPerSecond, vPerSecond, uOffset, vOffset, clampFix);
        Assert.True(EnbW3dMapperRuntimeProgramCompiler.TryCompile(mapper, out EnbW3dMapperRuntimeProgram program, out string? diagnostic), diagnostic);
        return program;
    }

    private static void AssertVector2(Vector2 expected, Vector2 actual, float tolerance = 1e-6f)
        => Assert.InRange(Vector2.Distance(expected, actual), 0f, tolerance);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EnbNext.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate EnbNext repository root from test output directory.");
    }
}
