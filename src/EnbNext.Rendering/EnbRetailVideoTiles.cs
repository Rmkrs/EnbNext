using System.Numerics;

namespace EnbNext.Rendering;

public sealed record EnbRetailVideoTile(int X, int Y, int Width, int Height, int TextureWidth, int TextureHeight,
    Vector4 Destination, Vector4 Uv);

/// <summary>A16560's overlapping RGB565 tiles and normalized full-view placement.</summary>
public static class EnbRetailVideoTiles
{
    public static IReadOnlyList<EnbRetailVideoTile> Create(int width, int height, int maximumWidth, int maximumHeight)
    {
        if (width <= 0 || height <= 0 || maximumWidth < 3 || maximumHeight < 3) throw new InvalidDataException("Invalid video texture dimensions.");
        static int Power(int value) { int n = 1; while (n < value) n = checked(n * 2); return n; }
        int tw = Math.Min(Power(width), maximumWidth), th = Math.Min(Power(height), maximumHeight);
        if (tw < 2 || th < 2) throw new InvalidDataException("Video texture stride is zero.");
        List<EnbRetailVideoTile> result = [];
        for (int y = 0; y < height; y += th - 1)
            for (int x = 0; x < width; x += tw - 1)
            {
                int w = Math.Min(tw, width - x), h = Math.Min(th, height - y);
                result.Add(new(x, y, w, h, tw, th, new((float)x / width, (float)y / height, (float)(x + w) / width, (float)(y + h) / height),
                    new(1f / tw, 1f / th, w == tw ? 1 : (float)(w - 1) / tw, h == th ? 1 : (float)(h + 1) / th)));
            }
        // Native reserves using stride tile-2, constructs with tile-1, then
        // draws the reserved count. Only constructed records are safe to draw.
        return result;
    }

    public static byte[] Rgb565(EnbRetailVideoTile tile, byte[] source, int movieWidth)
    {
        byte[] pixels = new byte[checked(tile.TextureWidth * tile.TextureHeight * 2)];
        for (int y = 0; y < tile.Height; y++)
            source.AsSpan(((y + tile.Y) * movieWidth + tile.X) * 2, tile.Width * 2).CopyTo(pixels.AsSpan(y * tile.TextureWidth * 2));
        return pixels;
    }
}
