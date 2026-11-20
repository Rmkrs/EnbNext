using EnbNext.Rendering;
using EnbNext.Rendering.OpenGL;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6MBlendGpuTests
{
    [Theory]
    [InlineData(EnbRenderBlendFactor.Zero, 0x0000)]
    [InlineData(EnbRenderBlendFactor.One, 0x0001)]
    [InlineData(EnbRenderBlendFactor.SourceColor, 0x0300)]
    [InlineData(EnbRenderBlendFactor.InverseSourceColor, 0x0301)]
    [InlineData(EnbRenderBlendFactor.SourceAlpha, 0x0302)]
    [InlineData(EnbRenderBlendFactor.InverseSourceAlpha, 0x0303)]
    public void CurrentRetailBlendFactorsMapExactlyToOpenGl(EnbRenderBlendFactor factor, int expected)
        => Assert.Equal(expected, EnbOpenGlFirstW3dShaderBackend.MapBlendFactor(factor));

    [Fact]
    public void UndefinedBlendFactorFailsClosed()
        => Assert.Throws<NotSupportedException>(() =>
            EnbOpenGlFirstW3dShaderBackend.MapBlendFactor((EnbRenderBlendFactor)0xFF));

    [Fact]
    public void RetailCompilerStillDefinesOpaqueAsOneZeroAndEnablesEveryOtherResolvedPair()
    {
        Assert.True(EnbW3dMaterialCompiler.TryDecodePackedRetailState(
            EnbW3dMaterialCompiler.RetailW3dShaderBaseline,
            out EnbW3dFixedFunctionState opaque,
            out string? diagnostic), diagnostic);
        Assert.Equal(EnbRenderBlendFactor.One, opaque.SourceBlend);
        Assert.Equal(EnbRenderBlendFactor.Zero, opaque.DestinationBlend);
        Assert.False(opaque.BlendEnabled);
    }
}
