using System.Numerics;
using EnbNext.Rendering;
using EnbNext.Rendering.OpenGL;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6OFogGpuTests
{
    [Fact]
    public void CurrentRetailFogModesMapToExactGpuState()
    {
        var scene = new EnbW3dSceneFogState(true, 0x80_40_20u, 100f, 300f);
        EnbW3dFogPipelineSemantics depth = EnbW3dFogPipelineSemantics.FromCurrentRetailRangeCapability(false);
        EnbW3dFogPipelineSemantics range = EnbW3dFogPipelineSemantics.FromCurrentRetailRangeCapability(true);

        EnbOpenGlFogUniformState sceneColor = EnbOpenGlFirstW3dShaderBackend.MapFog(
            EnbW3dFogMode.SceneColor, scene, rendererSupportsFog: true, pipeline: depth);
        Assert.True(sceneColor.Enabled);
        AssertClose(new Vector3(0x80 / 255f, 0x40 / 255f, 0x20 / 255f), sceneColor.Color);
        Assert.Equal(100f, sceneColor.Start);
        Assert.Equal(300f, sceneColor.End);
        Assert.Equal(EnbW3dFogDistanceSource.AbsoluteCameraZ, sceneColor.DistanceSource);

        EnbOpenGlFogUniformState black = EnbOpenGlFirstW3dShaderBackend.MapFog(
            EnbW3dFogMode.Black, scene, rendererSupportsFog: true, pipeline: range);
        Assert.True(black.Enabled);
        AssertClose(Vector3.Zero, black.Color);
        Assert.Equal(EnbW3dFogDistanceSource.CameraRange, black.DistanceSource);

        EnbOpenGlFogUniformState white = EnbOpenGlFirstW3dShaderBackend.MapFog(
            EnbW3dFogMode.White, scene, rendererSupportsFog: true, pipeline: depth);
        Assert.True(white.Enabled);
        AssertClose(Vector3.One, white.Color);

        Assert.False(EnbOpenGlFirstW3dShaderBackend.MapFog(
            EnbW3dFogMode.Disabled, scene, rendererSupportsFog: true, pipeline: depth).Enabled);
        Assert.False(EnbOpenGlFirstW3dShaderBackend.MapFog(
            EnbW3dFogMode.SceneColor, scene with { Enabled = false }, rendererSupportsFog: true, pipeline: depth).Enabled);
        Assert.False(EnbOpenGlFirstW3dShaderBackend.MapFog(
            EnbW3dFogMode.SceneColor, scene, rendererSupportsFog: false, pipeline: depth).Enabled);
    }

    [Theory]
    [InlineData(EnbW3dFogMode.SceneColor)]
    [InlineData(EnbW3dFogMode.Black)]
    [InlineData(EnbW3dFogMode.White)]
    public void PlanetZeroIntervalKeepsFogEnabledWithoutRejectingTheFrame(EnbW3dFogMode mode)
    {
        var scene = new EnbW3dSceneFogState(true, 0x123456, 0, 0);
        var pipeline = EnbW3dFogPipelineSemantics.FromCurrentRetailRangeCapability(true);
        var mapped = EnbOpenGlFirstW3dShaderBackend.MapFog(mode, scene, true, pipeline);
        Assert.True(mapped.Enabled);
        Assert.Equal(0, mapped.Start);
        Assert.Equal(0, mapped.End);
        var resolved = EnbW3dResolvedFogState.ResolveCurrentRetail(mode, scene, true, pipeline);
    }

    [Fact]
    public void UndefinedOrInvalidEnabledFogFailsClosed()
    {
        EnbW3dFogPipelineSemantics pipeline = EnbW3dFogPipelineSemantics.FromCurrentRetailRangeCapability(false);
        var validScene = new EnbW3dSceneFogState(true, 0x12_34_56u, 100f, 300f);

        Assert.Throws<NotSupportedException>(() => EnbOpenGlFirstW3dShaderBackend.MapFog(
            (EnbW3dFogMode)0xFF, validScene, rendererSupportsFog: true, pipeline: pipeline));
        Assert.Throws<NotSupportedException>(() => EnbOpenGlFirstW3dShaderBackend.MapFog(
            EnbW3dFogMode.SceneColor,
            validScene,
            rendererSupportsFog: true,
            pipeline: new EnbW3dFogPipelineSemantics((EnbW3dFogDistanceSource)0xFF)));

        foreach (EnbW3dSceneFogState invalidScene in new[]
        {
            validScene with { Start = 100f, End = 100f },
            validScene with { Start = 300f, End = 100f },
            validScene with { Start = float.NaN },
            validScene with { End = float.PositiveInfinity },
        })
        {
            Assert.Throws<NotSupportedException>(() => EnbOpenGlFirstW3dShaderBackend.MapFog(
                EnbW3dFogMode.SceneColor, invalidScene, rendererSupportsFog: true, pipeline: pipeline));
        }
    }

    [Fact]
    public void InvalidSceneIntervalIsIrrelevantWhenFogIsNotEffective()
    {
        var invalidScene = new EnbW3dSceneFogState(true, 0x12_34_56u, 100f, 100f);
        EnbW3dFogPipelineSemantics pipeline = EnbW3dFogPipelineSemantics.FromCurrentRetailRangeCapability(false);

        Assert.False(EnbOpenGlFirstW3dShaderBackend.MapFog(
            EnbW3dFogMode.Disabled, invalidScene, rendererSupportsFog: true, pipeline: pipeline).Enabled);
        Assert.False(EnbOpenGlFirstW3dShaderBackend.MapFog(
            EnbW3dFogMode.SceneColor, invalidScene with { Enabled = false }, rendererSupportsFog: true, pipeline: pipeline).Enabled);
        Assert.False(EnbOpenGlFirstW3dShaderBackend.MapFog(
            EnbW3dFogMode.SceneColor, invalidScene, rendererSupportsFog: false, pipeline: pipeline).Enabled);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, 1e-6f);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, 1e-6f);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, 1e-6f);
    }
}
