using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Height-field receiver geometry selected by retail 00465D30.</summary>
public static class EnbRetailTerrainProjectorGeometry
{
    /// <summary>00477E20's vertex submission. Backends without legacy ZBIAS
    /// use the native world-Z offset; this is separate from footprint selection.</summary>
    public static EnbW3dRenderMesh BuildMesh(EnbRetailTerrain terrain,
        EnbCurrentRetailShadowProjection projection, bool supportsLegacyDepthBias,
        EnbW3dRenderTextureBinding shadow, EnbW3dRenderTextureBinding gradient, float intensity)
    {
        var positions = Build(terrain, projection.VolumeCenter, projection.VolumeHalfExtent,
            projection.WorldFromProjector);
        if (!supportsLegacyDepthBias)
            for (int i = 0; i < positions.Length; i++) positions[i].Z += 3;
        var indices = Enumerable.Range(0, positions.Length).Select(i => (uint)i).ToArray();
        Vector3 min = positions.Aggregate(Vector3.Min), max = positions.Aggregate(Vector3.Max);
        var mesh = new EnbW3dRenderMesh(0, "terrain projector", $"land{terrain.Sector}",
            Vector3.Distance(min,max)*.5f, positions, new Vector3[positions.Length], [], indices,
            new EnbW3dRenderTriangleMetadata[positions.Length/3], [], [])
            { BoundingBoxMin=min, BoundingBoxMax=max, BoundingSphereCenter=(min+max)*.5f,
                CurrentRetailDepthBias=supportsLegacyDepthBias?1u:0u };
        var material = EnbCurrentRetailShadowMaterial.Bind(mesh, shadow, gradient, intensity);
        return (mesh).Copy().SetMaterialPasses([material]);
    }

    public static Vector3[] Build(EnbRetailTerrain terrain, Vector3 center,
        Vector3 halfExtent, Matrix4x4 orientation)
    {
        // The native input is an oriented box: three basis rows, center and
        // half extents. System.Numerics uses the transposed, row-vector basis.
        float extentX = MathF.Abs(orientation.M11 * halfExtent.X) +
            MathF.Abs(orientation.M21 * halfExtent.Y) + MathF.Abs(orientation.M31 * halfExtent.Z);
        float extentY = MathF.Abs(orientation.M12 * halfExtent.X) +
            MathF.Abs(orientation.M22 * halfExtent.Y) + MathF.Abs(orientation.M32 * halfExtent.Z);
        float step = terrain.HorizontalScale / terrain.Width;
        float minX = (center.X - extentX) / step + terrain.Width / 2;
        float maxX = (center.X + extentX) / step + terrain.Width / 2;
        float minY = (center.Y - extentY) / step + terrain.Width / 2;
        float maxY = (center.Y + extentY) / step + terrain.Width / 2;
        // 00465E8F..00465ECE expands by one cell BEFORE __ftol (truncation
        // toward zero, including outside the terrain). Endpoints are inclusive.
        int x0 = checked((int)(MathF.Min(minX, maxX) - 1));
        int x1 = checked((int)(MathF.Max(minX, maxX) + 1));
        int y0 = checked((int)(MathF.Min(minY, maxY) - 1));
        int y1 = checked((int)(MathF.Max(minY, maxY) + 1));
        var vertices = new Vector3[checked(checked(x1 - x0 + 1) * checked(y1 - y0 + 1) * 6)];
        int offset = 0;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            // Positions keep the unwrapped cell coordinates; only height
            // samples wrap. Compute adjacent XY by addition as 00465F30 does.
            Vector3 sw = terrain.Position(x, y);
            Vector3 se = new(sw.X + step, sw.Y, terrain.Position(x + 1, y).Z);
            Vector3 ne = new(sw.X + step, sw.Y + step, terrain.Position(x + 1, y + 1).Z);
            Vector3 nw = new(sw.X, sw.Y + step, terrain.Position(x, y + 1).Z);
            vertices[offset++] = sw; vertices[offset++] = ne; vertices[offset++] = nw;
            vertices[offset++] = sw; vertices[offset++] = se; vertices[offset++] = ne;
        }
        return vertices;
    }
}
