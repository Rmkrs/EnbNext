using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbModernHud
{
    /// <summary>Width of a bar's end stop (modern units).</summary>
    public const double Stop = 6;
    private EnbUiRaster? leftStop, rightStop;
    /// <summary>The bar end stops (left, right) once loaded, for other Modern UI bars.</summary>
    public (EnbUiRaster Left, EnbUiRaster Right)? BarStops => leftStop is { } l && rightStop is { } r ? (l, r) : null;

    /// <summary>
    /// The end stops of the cockpit reactor bar (TSI01A BA), cut from its texture: the bright cap columns at each
    /// end of the mesh's texture region, over the rows where the caps are lit. The Modern UI draws its bars as
    /// stop, gauge, stop with nothing of the bar's dark channel around the gauge.
    /// </summary>
    private void LoadStops()
    {
        var mesh = pieces[(int)Piece.EnergyHousing].Asset.Resource.Meshes[0];
        if (mesh.CurrentRetailLoadedTextureNames is not [var name, ..] || mesh.RetailTextureCoordinateSets.FirstOrDefault(s => s.Index == 0) is not { } set ||
            !assets.Textures.TryReadCurrentRetailSource(name, false, out var bytes, out _) || !EnbRetailTextureDecoder.TryDecode(bytes, name, out var texture)) return;
        var uv = set.Coordinates.ToArray(); if (uv.Length == 0) return;
        int u0 = (int)Math.Round(uv.Min(c => c.X) * texture.Width), u1 = (int)Math.Round(uv.Max(c => c.X) * texture.Width);
        int v0 = (int)Math.Floor(uv.Min(c => c.Y) * texture.Height), v1 = (int)Math.Ceiling(uv.Max(c => c.Y) * texture.Height);
        u0 = Math.Clamp(u0, 0, texture.Width - 1); u1 = Math.Clamp(u1, u0 + 1, texture.Width); v0 = Math.Clamp(v0, 0, texture.Height - 1); v1 = Math.Clamp(v1, v0 + 1, texture.Height);
        int width = texture.Width;
        int Lum(int x, int y) { var c = texture.Pixels.Span[y * width + x]; return c.A < 40 ? 0 : (c.R + c.G + c.B) / 3; }
        // Lit rows: where the column just inside the left end is bright.
        int probe = Math.Min(u0 + 3, u1 - 1), top = -1, bottom = -1;
        for (int y = v0; y < v1; y++) if (Lum(probe, y) >= 90) { if (top < 0) top = y; bottom = y; }
        if (top < 0) return;
        int middle = (top + bottom) / 2;
        // Skip any transparent texel columns at the region's edges, then take each stop up to the channel's dark interior.
        while (u0 < u1 && Lum(u0, middle) < 40) u0++;
        while (u1 > u0 && Lum(u1 - 1, middle) < 40) u1--;
        int leftEnd = u0; while (leftEnd < u1 && Lum(leftEnd, middle) >= 40) leftEnd++;
        int rightStart = u1; while (rightStart > leftEnd && Lum(rightStart - 1, middle) >= 40) rightStart--;
        if (leftEnd <= u0 || rightStart >= u1) return;
        leftStop = Crop(u0, leftEnd); rightStop = Crop(rightStart, u1);

        EnbUiRaster Crop(int x0, int x1)
        {
            var crop = new EnbColor32[(x1 - x0) * (bottom + 1 - top)];
            var source = texture.Pixels.Span;
            for (int y = top; y <= bottom; y++) for (int x = x0; x < x1; x++) crop[(y - top) * (x1 - x0) + x - x0] = source[y * width + x];
            return EnbUiRaster.FromPixels(x1 - x0, bottom + 1 - top, crop);
        }
    }
}
