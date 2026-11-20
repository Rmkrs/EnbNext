using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR9AhCurrentRetailEnvironmentAtlasTests
{
    [Fact]
    public void ProceduralGridUsesWrappedTriangleTilesFullBottomRightTileAndBoxMips()
    {
        EnbEnvironmentMapCatalog catalog = EnbEnvironmentMapCatalog.ParseText(
            """
            [PROCTEX-00]
            BaseTexture=grid_env01.tga

            [BACK-378]
            Texture=sky_env15.tga
            Axis=Y
            """);
        Assert.True(catalog.TryResolveReflection(378, out EnbEnvironmentReflectionDefinition reflection));

        EnbTextureImage grid = Image(8, 8, (_, _) => new EnbColor32(10, 20, 30, 0));
        EnbTextureImage sky = Image(4, 4, (x, y) =>
        {
            byte value = checked((byte)((y * 4) + x));
            return new EnbColor32(value, checked((byte)(value * 2)), checked((byte)(value * 3)), 0);
        });
        var images = new Dictionary<string, EnbTextureImage>(StringComparer.OrdinalIgnoreCase)
        {
            ["grid_env01.tga"] = grid,
            ["sky_env15.tga"] = sky,
        };

        bool built = EnbCurrentRetailEnvironmentAtlasRuntime.TryBuild(
            catalog,
            reflection,
            textureReduction: 0,
            logicalName => images.GetValueOrDefault(logicalName),
            out EnbCurrentRetailEnvironmentAtlasSet atlasSet,
            out string? diagnostic);

        Assert.True(built, diagnostic);
        Assert.Equal("sky_env15.tga", atlasSet.ReflectionTexture);
        Assert.Equal(4, atlasSet.ReflectionWidth);
        Assert.Equal(4, atlasSet.ReflectionHeight);
        Assert.True(atlasSet.TryResolve("GRID_ENV01.TGA", out EnbTextureImage atlas));
        Assert.False(atlasSet.TryResolve("PU5001.TGA", out _));
        Assert.Equal(EnbTextureFormat.CurrentRetailRuntimeRgb8, atlas.Format);
        Assert.Equal([(8, 8), (4, 4), (2, 2), (1, 1)],
            atlas.MipLevels.Select(static mip => (mip.Width, mip.Height)).ToArray());

        // The exact 4->2 wrapped triangle result for source values 0..15 is
        // [4,5;10,11]. Retail adds it at 2x over each of the twelve small cells.
        Assert.Equal(new EnbColor32(18, 36, 52, 255), atlas.GetPixel(0, 0));
        Assert.Equal(new EnbColor32(32, 66, 98, 255), atlas.GetPixel(3, 3));

        // The bottom-right 4x4 quadrant receives the full-size reflection once.
        Assert.Equal(new EnbColor32(10, 20, 30, 255), atlas.GetPixel(4, 4));
        Assert.Equal(new EnbColor32(40, 80, 120, 255), atlas.GetPixel(7, 7));

        // Replacement mips are generated from the completed atlas with the
        // current-retail 2x2 box filter and half-up RGB8 quantization.
        Assert.Equal(new EnbColor32(25, 51, 75, 255), atlas.MipLevels[1].GetPixel(0, 0));
    }

    [Fact]
    public void TwoTimesReflectionAddSaturatesEachRgbChannel()
    {
        EnbEnvironmentMapCatalog catalog = EnbEnvironmentMapCatalog.ParseText(
            """
            [PROCTEX-00]
            BaseTexture=grid_env01.tga

            [BACK-1]
            Texture=sky.tga
            """);
        Assert.True(catalog.TryResolveReflection(1, out EnbEnvironmentReflectionDefinition reflection));
        EnbTextureImage grid = Image(4, 4, (_, _) => new EnbColor32(250, 1, 100, 0));
        EnbTextureImage sky = Image(2, 2, (_, _) => new EnbColor32(10, 200, 100, 0));

        Assert.True(EnbCurrentRetailEnvironmentAtlasRuntime.TryBuild(
            catalog,
            reflection,
            textureReduction: 0,
            name => name.Equals("sky.tga", StringComparison.OrdinalIgnoreCase) ? sky : grid,
            out EnbCurrentRetailEnvironmentAtlasSet atlasSet,
            out string? diagnostic), diagnostic);
        Assert.True(atlasSet.TryResolve("grid_env01.tga", out EnbTextureImage atlas));
        Assert.Equal(new EnbColor32(255, 255, 255, 255), atlas.GetPixel(0, 0));
    }

    [Fact]
    public void SourceReductionIsAppliedBeforeAtlasBuildAndNotAppliedAgainAtUpload()
    {
        EnbEnvironmentMapCatalog catalog = EnbEnvironmentMapCatalog.ParseText(
            """
            [PROCTEX-00]
            BaseTexture=grid.tga

            [BACK-1]
            Texture=sky.tga
            """);
        Assert.True(catalog.TryResolveReflection(1, out EnbEnvironmentReflectionDefinition reflection));
        EnbTextureImage grid = EnbTextureImage.CreateCurrentRetailRuntimeRgb8(
        [
            (8, 8, SolidPixels(8, 8, 1)),
            (4, 4, SolidPixels(4, 4, 10)),
        ]);
        EnbTextureImage sky = EnbTextureImage.CreateCurrentRetailRuntimeRgb8(
        [
            (4, 4, SolidPixels(4, 4, 2)),
            (2, 2, SolidPixels(2, 2, 5)),
        ]);

        Assert.True(EnbCurrentRetailEnvironmentAtlasRuntime.TryBuild(
            catalog,
            reflection,
            textureReduction: 1,
            name => name.Equals("sky.tga", StringComparison.OrdinalIgnoreCase) ? sky : grid,
            out EnbCurrentRetailEnvironmentAtlasSet atlasSet,
            out string? diagnostic), diagnostic);
        Assert.Equal(1, atlasSet.SourceTextureReduction);
        Assert.True(atlasSet.TryResolve("grid.tga", out EnbTextureImage atlas));
        Assert.Equal((4, 4), (atlas.Width, atlas.Height));
        Assert.Equal(new EnbColor32(20, 20, 20, 255), atlas.GetPixel(0, 0));

        EnbTextureUploadMipPlan upload = EnbTextureUploadMipPlanner.Compile(
            atlas,
            EnbRenderTextureRuntimeSettings.FromRetailValues(1, 0));
        Assert.Equal(0, upload.FirstSourceMip);
        Assert.Equal(4, upload.Width);
        Assert.Equal(atlas.MipLevels.Count, upload.UploadMipCount);
    }

    [Fact]
    public void ProceduralGridDimensionMismatchFailsClosed()
    {
        EnbEnvironmentMapCatalog catalog = EnbEnvironmentMapCatalog.ParseText(
            """
            [PROCTEX-00]
            BaseTexture=grid_env01.tga

            [BACK-1]
            Texture=sky.tga
            """);
        Assert.True(catalog.TryResolveReflection(1, out EnbEnvironmentReflectionDefinition reflection));
        EnbTextureImage grid = Image(7, 8, (_, _) => new EnbColor32(0, 0, 0, 255));
        EnbTextureImage sky = Image(4, 4, (_, _) => new EnbColor32(0, 0, 0, 255));

        bool built = EnbCurrentRetailEnvironmentAtlasRuntime.TryBuild(
            catalog,
            reflection,
            textureReduction: 0,
            name => name.Equals("sky.tga", StringComparison.OrdinalIgnoreCase) ? sky : grid,
            out _,
            out string? diagnostic);

        Assert.False(built);
        Assert.Contains("requires exactly 2x", diagnostic, StringComparison.Ordinal);
    }

    private static EnbTextureImage Image(
        int width,
        int height,
        Func<int, int, EnbColor32> pixel)
    {
        var pixels = new EnbColor32[checked(width * height)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
                pixels[(y * width) + x] = pixel(x, y);
        }

        return EnbTextureImage.CreateCurrentRetailRuntimeRgb8(
            [(width, height, pixels)]);
    }

    private static EnbColor32[] SolidPixels(int width, int height, byte value)
        => Enumerable.Repeat(new EnbColor32(value, value, value, 0), checked(width * height)).ToArray();
}
