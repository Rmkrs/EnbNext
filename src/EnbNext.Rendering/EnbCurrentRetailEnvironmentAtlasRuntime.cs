using System.Collections.Immutable;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public sealed record EnbCurrentRetailEnvironmentAtlasSet(
    string ReflectionTexture,
    int ReflectionWidth,
    int ReflectionHeight,
    int SourceTextureReduction,
    ImmutableDictionary<string, EnbTextureImage> ProceduralTextures)
{
    public bool TryResolve(string logicalName, out EnbTextureImage image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        return ProceduralTextures.TryGetValue(logicalName.Trim(), out image!);
    }
}

/// <summary>
/// Rebuilds the current-retail PROCTEX grid surfaces from envmaps.ini. Retail
/// converts both inputs to RGB8, triangle-filters the selected BACK texture to
/// half size, adds twelve half-size tiles plus one full-size bottom-right tile
/// to the authored grid with a fixed 2x saturating add, then box-generates a
/// complete mip chain for the replacement texture.
/// </summary>
public static class EnbCurrentRetailEnvironmentAtlasRuntime
{
    private const int ReflectionAddMultiplier = 2;

    public static bool TryBuild(
        EnbEnvironmentMapCatalog catalog,
        EnbEnvironmentReflectionDefinition reflection,
        int textureReduction,
        Func<string, EnbTextureImage?> imageLookup,
        out EnbCurrentRetailEnvironmentAtlasSet atlasSet,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(reflection);
        ArgumentNullException.ThrowIfNull(imageLookup);
        if (textureReduction < 0)
            throw new ArgumentOutOfRangeException(nameof(textureReduction));
        atlasSet = default!;
        diagnostic = null;

        EnbTextureImage? reflectionImage = imageLookup(reflection.Texture);
        if (reflectionImage is null)
        {
            diagnostic = $"Selected reflection texture '{reflection.Texture}' is unavailable or undecodable.";
            return false;
        }
        EnbTextureMipLevel reflectionMip = reflectionImage.MipLevels[
            Math.Min(textureReduction, reflectionImage.MipLevels.Count - 1)];
        if (reflectionMip.Width < 2 || reflectionMip.Height < 2 ||
            (reflectionMip.Width & 1) != 0 || (reflectionMip.Height & 1) != 0)
        {
            diagnostic = $"Selected reflection texture '{reflection.Texture}' reduction {textureReduction} is {reflectionMip.Width}x{reflectionMip.Height}; current retail requires even dimensions of at least 2x2.";
            return false;
        }

        var atlases = ImmutableDictionary.CreateBuilder<string, EnbTextureImage>(StringComparer.OrdinalIgnoreCase);
        foreach (EnbEnvironmentProceduralTexture procedural in catalog.ProceduralTextures)
        {
            EnbTextureImage? grid = imageLookup(procedural.BaseTexture);
            if (grid is null)
            {
                diagnostic = $"Procedural base texture '{procedural.BaseTexture}' is unavailable or undecodable.";
                return false;
            }
            EnbTextureMipLevel gridMip = grid.MipLevels[Math.Min(textureReduction, grid.MipLevels.Count - 1)];
            if (gridMip.Width != checked(reflectionMip.Width * 2) ||
                gridMip.Height != checked(reflectionMip.Height * 2))
            {
                diagnostic =
                    $"Procedural base texture '{procedural.BaseTexture}' reduction {textureReduction} is {gridMip.Width}x{gridMip.Height}; " +
                    $"current retail requires exactly 2x the selected {reflectionMip.Width}x{reflectionMip.Height} reflection texture.";
                return false;
            }

            atlases[procedural.BaseTexture] = BuildAtlas(gridMip, reflectionMip);
        }

        if (atlases.Count == 0)
        {
            diagnostic = "envmaps.ini does not define any PROCTEX base textures.";
            return false;
        }

        atlasSet = new EnbCurrentRetailEnvironmentAtlasSet(
            reflection.Texture,
            reflectionMip.Width,
            reflectionMip.Height,
            textureReduction,
            atlases.ToImmutable());
        return true;
    }

    private static EnbTextureImage BuildAtlas(EnbTextureMipLevel grid, EnbTextureMipLevel reflection)
    {
        int width = grid.Width;
        int height = grid.Height;
        EnbColor32[] top = grid.Pixels.ToArray();
        for (int index = 0; index < top.Length; index++)
        {
            EnbColor32 source = top[index];
            top[index] = new EnbColor32(source.R, source.G, source.B, byte.MaxValue);
        }

        EnbColor32[] full = reflection.Pixels
            .ToArray()
            .Select(static color => new EnbColor32(color.R, color.G, color.B, byte.MaxValue))
            .ToArray();
        EnbColor32[] half = TriangleDownsample2XWrapped(
            full,
            reflection.Width,
            reflection.Height);
        int halfWidth = reflection.Width >> 1;
        int halfHeight = reflection.Height >> 1;

        AddSaturated(
            top,
            width,
            full,
            reflection.Width,
            reflection.Height,
            reflection.Width,
            reflection.Height);

        for (int cellY = 0; cellY < 4; cellY++)
        {
            for (int cellX = 0; cellX < 4; cellX++)
            {
                if (cellX >= 2 && cellY >= 2)
                    continue;
                AddSaturated(
                    top,
                    width,
                    half,
                    halfWidth,
                    halfHeight,
                    cellX * halfWidth,
                    cellY * halfHeight);
            }
        }

        var mips = new List<(int Width, int Height, IReadOnlyList<EnbColor32> Pixels)>
        {
            (width, height, top),
        };
        EnbColor32[] previous = top;
        int previousWidth = width;
        int previousHeight = height;
        while (previousWidth > 1 || previousHeight > 1)
        {
            EnbColor32[] next = BoxDownsample2X(previous, previousWidth, previousHeight);
            int nextWidth = Math.Max(1, previousWidth >> 1);
            int nextHeight = Math.Max(1, previousHeight >> 1);
            mips.Add((nextWidth, nextHeight, next));
            previous = next;
            previousWidth = nextWidth;
            previousHeight = nextHeight;
        }

        return EnbTextureImage.CreateCurrentRetailRuntimeRgb8(mips);
    }

    private static EnbColor32[] TriangleDownsample2XWrapped(
        IReadOnlyList<EnbColor32> source,
        int width,
        int height)
    {
        int targetWidth = width >> 1;
        int targetHeight = height >> 1;
        var target = new EnbColor32[checked(targetWidth * targetHeight)];
        ReadOnlySpan<int> weights = [1, 7, 7, 1];
        for (int y = 0; y < targetHeight; y++)
        {
            for (int x = 0; x < targetWidth; x++)
            {
                int r = 0;
                int g = 0;
                int b = 0;
                for (int ky = 0; ky < 4; ky++)
                {
                    int sourceY = Mod((2 * y) + ky - 1, height);
                    int wy = weights[ky];
                    for (int kx = 0; kx < 4; kx++)
                    {
                        int sourceX = Mod((2 * x) + kx - 1, width);
                        int weight = wy * weights[kx];
                        EnbColor32 color = source[(sourceY * width) + sourceX];
                        r += color.R * weight;
                        g += color.G * weight;
                        b += color.B * weight;
                    }
                }

                target[(y * targetWidth) + x] = new EnbColor32(
                    Quantize(r, 256),
                    Quantize(g, 256),
                    Quantize(b, 256),
                    byte.MaxValue);
            }
        }
        return target;
    }

    private static EnbColor32[] BoxDownsample2X(
        IReadOnlyList<EnbColor32> source,
        int width,
        int height)
    {
        int targetWidth = Math.Max(1, width >> 1);
        int targetHeight = Math.Max(1, height >> 1);
        var target = new EnbColor32[checked(targetWidth * targetHeight)];
        for (int y = 0; y < targetHeight; y++)
        {
            int y0 = Math.Min(height - 1, y * 2);
            int y1 = Math.Min(height - 1, y0 + 1);
            for (int x = 0; x < targetWidth; x++)
            {
                int x0 = Math.Min(width - 1, x * 2);
                int x1 = Math.Min(width - 1, x0 + 1);
                EnbColor32 a = source[(y0 * width) + x0];
                EnbColor32 b = source[(y0 * width) + x1];
                EnbColor32 c = source[(y1 * width) + x0];
                EnbColor32 d = source[(y1 * width) + x1];
                target[(y * targetWidth) + x] = new EnbColor32(
                    Quantize(a.R + b.R + c.R + d.R, 4),
                    Quantize(a.G + b.G + c.G + d.G, 4),
                    Quantize(a.B + b.B + c.B + d.B, 4),
                    byte.MaxValue);
            }
        }
        return target;
    }

    private static void AddSaturated(
        EnbColor32[] destination,
        int destinationWidth,
        IReadOnlyList<EnbColor32> source,
        int sourceWidth,
        int sourceHeight,
        int destinationX,
        int destinationY)
    {
        for (int y = 0; y < sourceHeight; y++)
        {
            int destinationRow = (destinationY + y) * destinationWidth;
            int sourceRow = y * sourceWidth;
            for (int x = 0; x < sourceWidth; x++)
            {
                int destinationIndex = destinationRow + destinationX + x;
                EnbColor32 target = destination[destinationIndex];
                EnbColor32 add = source[sourceRow + x];
                destination[destinationIndex] = new EnbColor32(
                    SaturatingAdd(target.R, add.R),
                    SaturatingAdd(target.G, add.G),
                    SaturatingAdd(target.B, add.B),
                    byte.MaxValue);
            }
        }
    }

    private static byte SaturatingAdd(byte target, byte add)
        => (byte)Math.Min(byte.MaxValue, target + (add * ReflectionAddMultiplier));

    private static byte Quantize(int numerator, int denominator)
        => checked((byte)((numerator + (denominator >> 1)) / denominator));

    private static int Mod(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}
