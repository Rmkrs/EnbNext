using EnbNext.Assets;

namespace EnbNext.Presentation;

/// <summary>
/// Decodes the dedicated current-retail gutter textures and applies the
/// gutter_color.ini render-object tint. GutterRadarGetIconAssetNameForObjectType
/// returns g_*.tga names, which the installed-resource resolver maps to the
/// authored 16x16 DXT5 DDS textures loaded onto gutter_icon.w3d.
/// </summary>
public static class EnbGutterIconRasterFactory
{
    public static bool TryCreate(
        EnbRetailAssetBundle assets,
        string iconAssetName,
        EnbRgb tint,
        int size,
        out EnbUiRaster bitmap)
    {
        ArgumentNullException.ThrowIfNull(assets);
        bitmap = default!;
        EnbMixResourceCatalog? resources = assets.RetailMixResources;
        if (resources is null)
            return false;

        EnbResolvedTextureResource resolved = new EnbRetailTextureResolver(resources).Resolve(iconAssetName);
        if (!resolved.IsResolved || resolved.Image is not EnbTextureImage image)
            return false;

        int outputSize = Math.Clamp(size, image.Width, 64);
        var pixels = new EnbColor32[checked(outputSize * outputSize)];
        for (int y = 0; y < outputSize; y++)
        {
            float sourceY = ((y + 0.5f) * image.Height / outputSize) - 0.5f;
            for (int x = 0; x < outputSize; x++)
            {
                float sourceX = ((x + 0.5f) * image.Width / outputSize) - 0.5f;
                EnbColor32 source = SampleBilinearClamp(image, sourceX, sourceY);
                pixels[(y * outputSize) + x] = new EnbColor32(
                    Modulate(source.R, tint.R),
                    Modulate(source.G, tint.G),
                    Modulate(source.B, tint.B),
                    source.A);
            }
        }

        bitmap = EnbUiRaster.FromPixels(outputSize, outputSize, pixels);
        return true;
    }

    private static EnbColor32 SampleBilinearClamp(EnbTextureImage image, float x, float y)
    {
        float floorX = MathF.Floor(x);
        float floorY = MathF.Floor(y);
        int x0 = Math.Clamp((int)floorX, 0, image.Width - 1);
        int y0 = Math.Clamp((int)floorY, 0, image.Height - 1);
        int x1 = Math.Min(x0 + 1, image.Width - 1);
        int y1 = Math.Min(y0 + 1, image.Height - 1);
        float tx = Math.Clamp(x - floorX, 0f, 1f);
        float ty = Math.Clamp(y - floorY, 0f, 1f);
        EnbColor32 c00 = image.GetPixel(x0, y0);
        EnbColor32 c10 = image.GetPixel(x1, y0);
        EnbColor32 c01 = image.GetPixel(x0, y1);
        EnbColor32 c11 = image.GetPixel(x1, y1);
        return new EnbColor32(
            Lerp(c00.R, c10.R, c01.R, c11.R, tx, ty),
            Lerp(c00.G, c10.G, c01.G, c11.G, tx, ty),
            Lerp(c00.B, c10.B, c01.B, c11.B, tx, ty),
            Lerp(c00.A, c10.A, c01.A, c11.A, tx, ty));
    }

    private static byte Lerp(byte c00, byte c10, byte c01, byte c11, float tx, float ty)
    {
        float top = c00 + ((c10 - c00) * tx);
        float bottom = c01 + ((c11 - c01) * tx);
        return (byte)Math.Clamp((int)MathF.Round(top + ((bottom - top) * ty)), 0, 255);
    }

    private static byte Modulate(byte source, float tint)
        => (byte)Math.Clamp((int)MathF.Round(source * Math.Clamp(tint, 0f, 1f)), 0, 255);
}
