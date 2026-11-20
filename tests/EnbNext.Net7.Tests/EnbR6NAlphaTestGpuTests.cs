using EnbNext.Rendering;
using EnbNext.Rendering.OpenGL;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6NAlphaTestGpuTests
{
    [Fact]
    public void CurrentRetailAlphaTestTuplesMapExactlyToShaderUniforms()
    {
        EnbOpenGlAlphaTestUniformState disabled = EnbOpenGlFirstW3dShaderBackend.MapAlphaTest(EnbW3dAlphaTestSemantics.Disabled);
        Assert.Equal(0f, disabled.Mode);
        Assert.Equal(0f, disabled.Reference);

        EnbOpenGlAlphaTestUniformState inverseSourceAlpha = EnbOpenGlFirstW3dShaderBackend.MapAlphaTest(
            new EnbW3dAlphaTestSemantics(true, EnbRenderDepthCompare.LessOrEqual, 0x9F));
        Assert.Equal(1f, inverseSourceAlpha.Mode);
        Assert.InRange(MathF.Abs((0x9F / 255f) - inverseSourceAlpha.Reference), 0f, 1e-6f);

        EnbOpenGlAlphaTestUniformState otherSourceBlend = EnbOpenGlFirstW3dShaderBackend.MapAlphaTest(
            new EnbW3dAlphaTestSemantics(true, EnbRenderDepthCompare.GreaterOrEqual, 0x60));
        Assert.Equal(2f, otherSourceBlend.Mode);
        Assert.InRange(MathF.Abs((0x60 / 255f) - otherSourceBlend.Reference), 0f, 1e-6f);
    }

    [Theory]
    [InlineData(EnbRenderDepthCompare.Greater, 0x9F)]
    [InlineData(EnbRenderDepthCompare.LessOrEqual, 0x60)]
    [InlineData(EnbRenderDepthCompare.GreaterOrEqual, 0x9F)]
    public void NonRetailAlphaTestTuplesFailClosed(EnbRenderDepthCompare compare, byte reference)
        => Assert.Throws<NotSupportedException>(() =>
            EnbOpenGlFirstW3dShaderBackend.MapAlphaTest(new EnbW3dAlphaTestSemantics(true, compare, reference)));
}
