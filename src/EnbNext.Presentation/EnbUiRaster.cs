using System.Collections.Immutable;

namespace EnbNext.Presentation;

/// <summary>
/// Immutable authored UI/loading raster uploaded to the OpenGL overlay surface.
/// World sky and stars use static GPU geometry, never this bitmap path.
/// </summary>
public sealed record EnbUiRaster(
    int Width,
    int Height,
    ImmutableArray<byte> BgraPixels)
{
    public static EnbUiRaster FromPixels(int width, int height, ReadOnlySpan<EnbNext.Assets.EnbColor32> pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height))
            throw new ArgumentException("Raster dimensions must match the supplied pixel count.", nameof(pixels));
        byte[] data = new byte[checked(pixels.Length * 4)];
        for (int i = 0; i < pixels.Length; i++)
        {
            var color = pixels[i];
            data[i * 4] = color.B; data[i * 4 + 1] = color.G;
            data[i * 4 + 2] = color.R; data[i * 4 + 3] = color.A;
        }
        return new(width, height, System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(data));
    }

    // Plain authored textures can retain native BC blocks. Composited/tinted
    // rasters continue to own their explicitly generated BGRA pixels.
    public EnbNext.Assets.EnbTextureImage? CompressedSource { get; init; }
    public bool IsValid =>
        Width > 0 &&
        Height > 0 &&
        BgraPixels.Length == checked(Width * Height * 4);
}

