using System.Numerics;
using EnbNext.Rendering;
using EnbNext.Rendering.OpenGL;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6LRandomizedMapperGpuTests
{
    [Fact]
    public void RetailMapperRandomSourceMatchesSeedExpansionTwistAndFirstTemperedWords()
    {
        var source = new EnbW3dRetailMapperRandomSource();
        uint[] expected =
        [
            0xD13C8AF5u, 0xFFC27482u, 0x82A6958Bu,
            0x21AC240Au, 0x09110BBAu, 0xFE127BC0u,
            0xA02E7212u, 0x10060698u, 0x692452F5u,
        ];

        foreach (uint value in expected)
            Assert.Equal(value, source.NextUInt32());

        source.Reseed(0u);
        var zeroFallback = new EnbW3dRetailMapperRandomSource(EnbW3dRetailMapperRandomSource.ZeroSeedFallback);
        Assert.Equal(zeroFallback.NextUInt32(), source.NextUInt32());
    }

    [Fact]
    public void Mode10ConstructorConsumesThreeSamplesAndWritesAuthoredTranslationAxes()
    {
        EnbW3dMapperRuntimeProgram program = Compile(
            "FPS=0",
            uScale: 2f,
            vScale: 3f,
            uPerSecond: 4f,
            vPerSecond: -6f);
        var source = new EnbW3dRetailMapperRandomSource();
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(program, 1000u, source, out EnbW3dMapperRuntimeState state, out string? diagnostic), diagnostic);

        const float firstAngle = 5.1354365f;
        const float firstOffset = 0.9990609f;
        const float secondOffset = 0.5103544f;
        float cosine = MathF.Cos(firstAngle);
        float sine = MathF.Sin(firstAngle);

        EnbW3dMapperRuntimeEvaluation initial = state.Evaluate(1000u);
        AssertVector(new Vector2(cosine * 2f, cosine * 3f), initial.TextureScale);
        AssertVector(new Vector2(-sine * 3f, sine * 2f), initial.TextureCross);
        AssertVector(new Vector2(firstOffset, secondOffset), initial.TextureOffset);

        EnbW3dMapperRuntimeEvaluation advanced = state.Evaluate(1250u);
        AssertVector(new Vector2(
            FmodOne(firstOffset + (250.0 * 0.004f)),
            FmodOne(secondOffset + (250.0 * -0.006f))), advanced.TextureOffset, 2e-6f);
    }

    [Fact]
    public void Mode10RefreshesOneTriplePerApplyAfterAnyNumberOfFpsBoundariesAndRetainsResidual()
    {
        EnbW3dMapperRuntimeProgram program = Compile(
            "FPS=2",
            uPerSecond: 1f,
            vPerSecond: 2f);
        var source = new EnbW3dRetailMapperRandomSource();
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(program, 1000u, source, out EnbW3dMapperRuntimeState state, out string? diagnostic), diagnostic);

        // 1250 ms crosses two 500 ms boundaries. Current retail refreshes one
        // random triple and retains 250 ms, rather than consuming two triples.
        EnbW3dMapperRuntimeEvaluation runtime = state.Evaluate(2250u);
        const float refreshedAngle = 0.8264457f;
        const float refreshedFirst = 0.03541635f;
        const float refreshedSecond = 0.99246955f;
        float cosine = MathF.Cos(refreshedAngle);
        float sine = MathF.Sin(refreshedAngle);
        AssertVector(new Vector2(cosine), runtime.TextureScale);
        AssertVector(new Vector2(-sine, sine), runtime.TextureCross);
        AssertVector(new Vector2(
            FmodOne(refreshedFirst + (250.0 * .001f)),
            FmodOne(refreshedSecond + (250.0 * .002f))), runtime.TextureOffset, 2e-6f);

        // Constructor = words 1..3, refresh = words 4..6. The next global
        // mapper RNG word must therefore still be word 7.
        Assert.Equal(0xA02E7212u, source.NextUInt32());
    }

    [Fact]
    public void Mode10AuthoredFpsRatePreservesNativeMulticlockResidual()
    {
        // Native randomProduction controls224/480 prove that constructor FMUL
        // AEDC40 and whole00938F20 leave a small positive residual at PC24.
        // The former division/double calculation incorrectly left another interval.
        var source=new EnbW3dRetailMapperRandomSource();
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(Compile("FPS=30"),100000u,source,out var state,out var error),error);
        state.Evaluate(100000u);state.Evaluate(100250u);
        var first=state.Evaluate(101000u);
        AssertVector(new Vector2(.06259195f,.4107105f),first.TextureOffset,2e-6f);
        var second=state.Evaluate(101000u);
        Assert.Equal(first,second);
        Assert.Equal(second,state.Evaluate(101000u));
    }

    [Fact]
    public void Mode10ResetRestartsElapsedClockWithoutRefreshingCurrentRandomState()
    {
        EnbW3dMapperRuntimeProgram program = Compile("FPS=4", uPerSecond: 1f, vPerSecond: -1f);
        var source = new EnbW3dRetailMapperRandomSource();
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(program, 1000u, source, out EnbW3dMapperRuntimeState state, out string? diagnostic), diagnostic);

        EnbW3dMapperRuntimeEvaluation before = state.Evaluate(1100u);
        state.Reset(2000u);
        EnbW3dMapperRuntimeEvaluation after = state.Evaluate(2000u);
        AssertVector(before.TextureScale, after.TextureScale);
        AssertVector(before.TextureCross, after.TextureCross);
        AssertVector(new Vector2(0.9990609f, 0.5103544f), after.TextureOffset, 2e-6f);

        // Reset consumed no RNG.
        Assert.Equal(0x21AC240Au, source.NextUInt32());
    }

    [Fact]
    public void Mode10StillFailsClosedWithoutExplicitRandomSourceButBackendOwnsOneSharedStream()
    {
        EnbW3dMapperRuntimeProgram program = Compile("FPS=2");
        Assert.False(EnbW3dMapperRuntimeState.TryCreate(program, 0u, out _, out string? diagnostic));
        Assert.Contains("explicit", diagnostic ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        string root = FindRepositoryRoot();
        string lane = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering", "EnbW3dProgrammableTextureLane.cs"));
        string backend = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering.OpenGL", "EnbOpenGlStage0TextureBackend.cs"));
        Assert.Contains("EnbW3dMapperRuntimeProgramKind.Randomized", lane, StringComparison.Ordinal);
        Assert.Contains("static readonly EnbW3dRetailMapperRandomSource s_mapperRandomSource", backend, StringComparison.Ordinal);
        Assert.Contains("runState.Mapper0Program", backend, StringComparison.Ordinal);
        Assert.Contains("runState.Mapper1Program", backend, StringComparison.Ordinal);
    }

    private static EnbW3dMapperRuntimeProgram Compile(
        string raw,
        float uScale = 1f,
        float vScale = 1f,
        float uPerSecond = 0f,
        float vPerSecond = 0f)
    {
        var mapper = new EnbW3dRenderMapper(
            EnbW3dMapperTypeSelector.Mode10,
            raw,
            uScale,
            vScale,
            uPerSecond,
            vPerSecond,
            UOffset: 0f,
            VOffset: 0f,
            ClampFix: false);
        Assert.True(EnbW3dMapperRuntimeProgramCompiler.TryCompile(mapper, out EnbW3dMapperRuntimeProgram program, out string? diagnostic), diagnostic);
        return program;
    }

    private static float FmodOne(double value)
        => (float)(value % 1.0);

    private static void AssertVector(Vector2 expected, Vector2 actual, float tolerance = 1e-6f)
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
        throw new DirectoryNotFoundException("Could not locate EnbNext repository root.");
    }
}
