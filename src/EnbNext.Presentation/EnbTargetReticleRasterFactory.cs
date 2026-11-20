using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Presentation;

/// <summary>
/// Rasterizes Retail's authored target-hover reticle from <c>HUD01A.W3D</c>.
/// Current <c>client.exe</c> target_reticle.cpp constructs this asset from the
/// <c>hud01a</c> render object and clones the named children UL/UR/BR/BL.
/// </summary>
public static class EnbTargetReticleRasterFactory
{
    private const string ModelResource = "hud01a.w3d";
    private const int PixelsPerDesignUnit = 4;
    private static readonly string[] RequiredMeshes = ["UL", "UR", "BR", "BL"];

    public static bool TryCreate(
        EnbRetailAssetBundle assets,
        EnbRgb tint,
        out EnbTargetReticleGlyphSet glyphs)
    {
        ArgumentNullException.ThrowIfNull(assets);
        glyphs = default!;
        EnbMixResourceCatalog? resources = assets.RetailMixResources;
        if (resources is null ||
            !resources.TryReadResource(ModelResource, out byte[] bytes, out _) ||
            !EnbW3dModel.TryDecode(bytes, out EnbW3dModel model))
        {
            return false;
        }

        var byName = new Dictionary<string, EnbTargetReticleGlyph>(StringComparer.Ordinal);
        foreach (string meshName in RequiredMeshes)
        {
            EnbW3dMesh? mesh = model.Meshes.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, meshName, StringComparison.Ordinal));
            if (mesh is null || !TryRasterizeMesh(resources, mesh, tint, out EnbTargetReticleGlyph glyph))
                return false;
            byName.Add(meshName, glyph);
        }

        glyphs = new EnbTargetReticleGlyphSet(
            byName["UL"],
            byName["UR"],
            byName["BR"],
            byName["BL"]);
        return true;
    }

    private static bool TryRasterizeMesh(
        EnbMixResourceCatalog resources,
        EnbW3dMesh mesh,
        EnbRgb tint,
        out EnbTargetReticleGlyph glyph)
    {
        glyph = default!;
        if (mesh.Vertices.Count == 0 || mesh.Triangles.Count == 0)
            return false;

        EnbW3dTextureStage? stage = mesh.MaterialPasses
            .SelectMany(static pass => pass.TextureStages)
            .FirstOrDefault();
        if (stage is null ||
            stage.RuntimeTexCoords.Count != mesh.Vertices.Count ||
            !stage.TryResolveSingleTextureName(mesh.TextureNames, out string textureName))
        {
            return false;
        }

        EnbResolvedTextureResource texture = new EnbRetailTextureResolver(resources).Resolve(textureName);
        if (texture.Image is not EnbTextureImage image)
            return false;

        Vector2 minimum = new(float.PositiveInfinity);
        Vector2 maximum = new(float.NegativeInfinity);
        foreach (Vector3 vertex in mesh.Vertices)
        {
            minimum = Vector2.Min(minimum, new Vector2(vertex.X, vertex.Y));
            maximum = Vector2.Max(maximum, new Vector2(vertex.X, vertex.Y));
        }

        Vector2 extent = maximum - minimum;
        if (!float.IsFinite(extent.X) || !float.IsFinite(extent.Y) || extent.X <= 0f || extent.Y <= 0f)
            return false;

        int width = Math.Max(1, (int)MathF.Ceiling(extent.X * PixelsPerDesignUnit));
        int height = Math.Max(1, (int)MathF.Ceiling(extent.Y * PixelsPerDesignUnit));
        var pixels = new EnbColor32[checked(width * height)];
        foreach (EnbW3dTriangle triangle in mesh.Triangles)
        {
            if (triangle.Vertex0 >= mesh.Vertices.Count ||
                triangle.Vertex1 >= mesh.Vertices.Count ||
                triangle.Vertex2 >= mesh.Vertices.Count)
            {
                return false;
            }

            int i0 = checked((int)triangle.Vertex0);
            int i1 = checked((int)triangle.Vertex1);
            int i2 = checked((int)triangle.Vertex2);
            RasterizeTriangle(
                pixels,
                width,
                height,
                ToPixel(mesh.Vertices[i0], minimum, maximum, width, height),
                ToPixel(mesh.Vertices[i1], minimum, maximum, width, height),
                ToPixel(mesh.Vertices[i2], minimum, maximum, width, height),
                stage.RuntimeTexCoords[i0],
                stage.RuntimeTexCoords[i1],
                stage.RuntimeTexCoords[i2],
                image,
                tint);
        }

        glyph = new EnbTargetReticleGlyph(
            EnbUiRaster.FromPixels(width, height, pixels),
            minimum.X,
            maximum.X,
            minimum.Y,
            maximum.Y);
        return true;
    }

    private static Vector2 ToPixel(Vector3 value, Vector2 minimum, Vector2 maximum, int width, int height)
        => new(
            (value.X - minimum.X) * (width - 1) / (maximum.X - minimum.X),
            (maximum.Y - value.Y) * (height - 1) / (maximum.Y - minimum.Y));

    private static void RasterizeTriangle(
        EnbColor32[] output,
        int width,
        int height,
        Vector2 p0,
        Vector2 p1,
        Vector2 p2,
        Vector2 uv0,
        Vector2 uv1,
        Vector2 uv2,
        EnbTextureImage texture,
        EnbRgb tint)
    {
        float denominator = Cross(p1 - p0, p2 - p0);
        if (MathF.Abs(denominator) <= 1e-6f)
            return;

        int minX = Math.Clamp((int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))), 0, width - 1);
        int maxX = Math.Clamp((int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))), 0, width - 1);
        int minY = Math.Clamp((int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))), 0, height - 1);
        int maxY = Math.Clamp((int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))), 0, height - 1);
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Vector2 point = new(x + 0.5f, y + 0.5f);
                float w1 = Cross(point - p0, p2 - p0) / denominator;
                float w2 = Cross(p1 - p0, point - p0) / denominator;
                float w0 = 1f - w1 - w2;
                if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f)
                    continue;

                Vector2 uv = (uv0 * w0) + (uv1 * w1) + (uv2 * w2);
                EnbColor32 source = SampleBilinearClamp(
                    texture,
                    uv.X * (texture.Width - 1),
                    uv.Y * (texture.Height - 1));
                int index = (y * width) + x;
                if (source.A > output[index].A)
                {
                    output[index] = new EnbColor32(
                        Modulate(source.R, tint.R),
                        Modulate(source.G, tint.G),
                        Modulate(source.B, tint.B),
                        source.A);
                }
            }
        }
    }

    private static EnbColor32 SampleBilinearClamp(EnbTextureImage image, float x, float y)
    {
        int x0 = Math.Clamp((int)MathF.Floor(x), 0, image.Width - 1);
        int y0 = Math.Clamp((int)MathF.Floor(y), 0, image.Height - 1);
        int x1 = Math.Min(x0 + 1, image.Width - 1);
        int y1 = Math.Min(y0 + 1, image.Height - 1);
        float tx = Math.Clamp(x - MathF.Floor(x), 0f, 1f);
        float ty = Math.Clamp(y - MathF.Floor(y), 0f, 1f);
        return new EnbColor32(
            Lerp(image.GetPixel(x0, y0).R, image.GetPixel(x1, y0).R, image.GetPixel(x0, y1).R, image.GetPixel(x1, y1).R, tx, ty),
            Lerp(image.GetPixel(x0, y0).G, image.GetPixel(x1, y0).G, image.GetPixel(x0, y1).G, image.GetPixel(x1, y1).G, tx, ty),
            Lerp(image.GetPixel(x0, y0).B, image.GetPixel(x1, y0).B, image.GetPixel(x0, y1).B, image.GetPixel(x1, y1).B, tx, ty),
            Lerp(image.GetPixel(x0, y0).A, image.GetPixel(x1, y0).A, image.GetPixel(x0, y1).A, image.GetPixel(x1, y1).A, tx, ty));
    }

    private static byte Lerp(byte c00, byte c10, byte c01, byte c11, float tx, float ty)
    {
        float top = c00 + ((c10 - c00) * tx);
        float bottom = c01 + ((c11 - c01) * tx);
        return (byte)Math.Clamp((int)MathF.Round(top + ((bottom - top) * ty)), 0, 255);
    }

    private static byte Modulate(byte source, float tint)
        => (byte)Math.Clamp((int)MathF.Round(source * Math.Clamp(tint, 0f, 1f)), 0, 255);

    private static float Cross(Vector2 left, Vector2 right)
        => (left.X * right.Y) - (left.Y * right.X);
}

public sealed record EnbTargetReticleGlyphSet(
    EnbTargetReticleGlyph UpperLeft,
    EnbTargetReticleGlyph UpperRight,
    EnbTargetReticleGlyph BottomRight,
    EnbTargetReticleGlyph BottomLeft);

public sealed record EnbTargetReticleGlyph(
    EnbUiRaster Raster,
    float MinX,
    float MaxX,
    float MinY,
    float MaxY)
{
    public float DesignWidth => MaxX - MinX;
    public float DesignHeight => MaxY - MinY;
}
