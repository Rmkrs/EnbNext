using System.Numerics;
using EnbNext.Rendering;
using EnbNext.Rendering.OpenGL;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6IBumpEnvironmentMapperGpuTests
{

    [Fact]
    public void Mode12RuntimePreservesUintTickWrapAndCurrentRetailClampBranchOrder()
    {
        EnbW3dMapperRuntimeProgram wrappedProgram = CompileMode12(
            uScale: 1f,
            vScale: 1f,
            uPerSecond: 1f,
            vPerSecond: 0f,
            uOffset: 0f,
            vOffset: 0f,
            clampFix: false,
            bumpRotation: 0f,
            bumpScale: 1f);
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(wrappedProgram, uint.MaxValue - 9u, out EnbW3dMapperRuntimeState wrapped, out string? diagnostic), diagnostic);
        EnbW3dMapperRuntimeEvaluation wrap = wrapped.Evaluate(10u);
        Assert.InRange(MathF.Abs(wrap.TextureOffset.X - 0.98f), 0f, 1e-6f);

        EnbW3dMapperRuntimeProgram clampedProgram = CompileMode12(
            uScale: 0.1f,
            vScale: 0.2f,
            uPerSecond: 100f,
            vPerSecond: -100f,
            uOffset: 0f,
            vOffset: 0f,
            clampFix: true,
            bumpRotation: 0f,
            bumpScale: 1f);
        Assert.True(EnbW3dMapperRuntimeState.TryCreate(clampedProgram, 100u, out EnbW3dMapperRuntimeState clamped, out diagnostic), diagnostic);
        EnbW3dMapperRuntimeEvaluation clamp = clamped.Evaluate(110u);
        AssertVector2(new Vector2(-0.1f, 0.2f), clamp.TextureOffset);
    }

    [Fact]
    public void Mode12RuntimeUsesExplicitRetailDownwardRoundingForBothSigns()
    {
        EnbW3dMapperRuntimeProgram program = CompileMode12(
            uScale: 1f,
            vScale: 1f,
            uPerSecond: 0f,
            vPerSecond: 0f,
            uOffset: 0.75f,
            vOffset: -0.75f,
            clampFix: false,
            bumpRotation: 0f,
            bumpScale: 1f);

        Assert.True(EnbW3dMapperRuntimeState.TryCreate(program, 100u, out EnbW3dMapperRuntimeState state, out string? diagnostic), diagnostic);
        EnbW3dMapperRuntimeEvaluation evaluation = state.Evaluate(100u);

        AssertVector2(new Vector2(0.75f, 0.25f), evaluation.TextureOffset);
    }

    [Fact]
    public void R6iRuntimeBoundaryStillFailsClosedForGeneratedOrRandomizedPrograms()
    {
        var mapper = new EnbW3dRenderMapper(
            EnbW3dMapperTypeSelector.Mode10,
            "FPS=1",
            1f,
            1f,
            0f,
            0f,
            0f,
            0f,
            false);
        Assert.True(EnbW3dMapperRuntimeProgramCompiler.TryCompile(mapper, out EnbW3dMapperRuntimeProgram randomized, out string? diagnostic), diagnostic);
        Assert.False(EnbW3dMapperRuntimeState.TryCreate(randomized, 0u, out _, out diagnostic));
        Assert.Contains("random", diagnostic ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gles3ShaderAppliesRuntimeMapper0Count2BeforeBumpSampling()
    {
        (string vertex, string fragment) = EnbOpenGlStage0TextureBackend.CreateShaderSources(
            new GlVersion(GlProfileType.OpenGLES, 3, 0));

        Assert.Contains("uniform vec4 uMapper0ScaleOffset", vertex, StringComparison.Ordinal);
        Assert.Contains("uniform vec4 uMapper0Cross", vertex, StringComparison.Ordinal);
        Assert.Contains("return affineMapper(passUv,scaleOffset,cross)", vertex, StringComparison.Ordinal);
        Assert.Contains("vTexCoord0=applyMapper(uMapper0Kind,uMapper0Source,uMapper0Axis,aTexCoord0,aPosition,worldPosition,worldNormal,uMapper0ScaleOffset,uMapper0Cross)", vertex, StringComparison.Ordinal);
        Assert.Contains("texture(uTexture0,stage0Coordinates(vTexCoord0))", fragment, StringComparison.Ordinal);
        Assert.Contains("uPerturbationMatrix", fragment, StringComparison.Ordinal);
        Assert.Contains("vTexCoord1+delta", fragment, StringComparison.Ordinal);
    }

    private static EnbW3dMapperRuntimeProgram CompileMode12(
        float uScale,
        float vScale,
        float uPerSecond,
        float vPerSecond,
        float uOffset,
        float vOffset,
        bool clampFix,
        float bumpRotation,
        float bumpScale)
    {
        var mapper = new EnbW3dRenderMapper(
            EnbW3dMapperTypeSelector.Mode12,
            FormattableString.Invariant($"BumpRotation={bumpRotation}\r\nBumpScale={bumpScale}"),
            uScale,
            vScale,
            uPerSecond,
            vPerSecond,
            uOffset,
            vOffset,
            clampFix);
        Assert.True(EnbW3dMapperRuntimeProgramCompiler.TryCompile(mapper, out EnbW3dMapperRuntimeProgram program, out string? diagnostic), diagnostic);
        return program;
    }

    private static void AssertBumpRotationScale(float angle, float scale, Vector4 actual)
    {
        Vector4 expected = new(
            MathF.Cos(angle) * scale,
            -MathF.Sin(angle) * scale,
            MathF.Sin(angle) * scale,
            MathF.Cos(angle) * scale);
        Assert.InRange(Vector4.Distance(expected, actual), 0f, 1e-5f);
    }

    private static void AssertVector2(Vector2 expected, Vector2 actual)
        => Assert.InRange(Vector2.Distance(expected, actual), 0f, 1e-6f);

}
