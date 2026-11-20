using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Terrain height field queries in the retail client's arithmetic: grid cells split into two
/// triangles along the diagonal, single-precision operations in the retail order, and the retail
/// reciprocal square root (three Newton steps from a fixed seed) for normals. Grid indices wrap.</summary>
public readonly struct EnbTerrainHeightField
{
    private readonly ushort[] heights;
    private readonly int width, mask, shift;
    private readonly float xScale, yScale, heightScale, half;

    private readonly bool wrapping;

    public EnbTerrainHeightField(int width, ReadOnlySpan<ushort> heights, float xScale, float yScale, float heightScale, bool wrapping = false)
    {
        if (width <= 0 || !BitOperations.IsPow2(width) || heights.Length != checked(width * width))
            throw new ArgumentException("Terrain height field must be square with a power-of-two width.");
        this.heights = heights.ToArray(); this.width = width; mask = width - 1; shift = BitOperations.Log2((uint)width);
        this.xScale = xScale; this.yScale = yScale; this.heightScale = heightScale; half = width / 2; this.wrapping = wrapping;
    }

    /// <summary>Normalized (u, v) in [0, 1] to a world point on the surface.</summary>
    public Vector3 World(float u, float v)
    {
        float x = (float)(u - 0.5) * xScale * width, y = (float)(v - 0.5) * yScale * width;
        return new(x, y, Height(new(x, y)));
    }

    /// <summary>World position to continuous grid coordinates.</summary>
    public Vector2 Grid(Vector2 location) => new(location.X / xScale + half, location.Y / yScale + half);

    /// <summary>Index of the decoration patch containing a world position.</summary>
    public int Patch(Vector2 location, int gridWidth)
    {
        if (gridWidth <= 0 || !BitOperations.IsPow2(gridWidth)) throw new ArgumentOutOfRangeException(nameof(gridWidth));
        var grid = Grid(location); uint m = (uint)gridWidth - 1;
        uint x = Truncate(Math.Floor(grid.X / gridWidth)), y = Truncate(Math.Floor(grid.Y / gridWidth));
        return checked((int)((uint)gridWidth * (y & m) + (x & m)));
        static uint Truncate(double value) => unchecked((uint)(long)value);
    }

    public float Height(Vector2 location)
    {
        Cell(location, out float fx, out float fy, out float h00, out float h10, out float h01, out float h11);
        if (fx < fy) return (1 - fy) * (h00 - h01) + (h11 - h01) * fx + h01;
        return (1 - fx) * (h00 - h10) + (h11 - h10) * fy + h10;
    }

    public Vector3 Normal(Vector2 location)
    {
        Cell(location, out float fx, out float fy, out float h00, out float h10, out float h01, out float h11);
        float x, y, z;
        if (fy > fx)
        {
            float d1 = h01 - h00, d2 = h11 - h00;
            x = yScale * d1 - d2 * yScale; y = -(d1 * xScale); z = yScale * xScale;
        }
        else
        {
            float d1 = h10 - h00, d2 = h11 - h00;
            x = -(yScale * d1); y = d1 * xScale - d2 * xScale; z = yScale * xScale;
        }
        if (y * y + z * z + x * x == 0) return new(x, y, z);
        float r = EnbRetailFloat.ReciprocalSquareRoot(z * z + y * y + x * x);
        return new(x * r, y * r, z * r);
    }

    /// <summary>First hit of the segment start..end with the surface, as a fraction of the segment, if
    /// nearer than <paramref name="fraction"/>. A start under the surface sets StartInside and reports no
    /// hit. Without wrapping, the segment is clipped to the grid first; cells are walked in order along the
    /// segment and the walk stops at the first cell with a hit.</summary>
    public (float Fraction, bool StartInside) CastSegment(Vector3 start, Vector3 end, float fraction = 1, bool startInside = false)
    {
        if (Height(new(start.X, start.Y)) > start.Z) return (fraction, true);
        Vector2 a = Grid(new(start.X, start.Y)), b = Grid(new(end.X, end.Y));
        Vector2 p = a, q = b;
        if (!wrapping)
        {
            // Retail clip: per-axis intervals within [0, width-1], combined and clamped to [0, 1]. Only a
            // start beyond the end or an end before the start is rejected; non-overlapping intervals are
            // not, so a segment passing beside the grid still walks (onto repeated heights).
            float limit = width - 1, dx = b.X - a.X, dy = b.Y - a.Y;
            float xMin = 0, xMax = 1, yMin = 0, yMax = 1;
            if (dx == 0) { if (a.X < 0 || !(a.X <= limit)) return (fraction, startInside); }
            else { xMin = -a.X / dx; xMax = (limit - a.X) / dx; if (!(xMin <= xMax)) (xMin, xMax) = (xMax, xMin); }
            if (dy == 0) { if (a.Y < 0 || !(a.Y <= limit)) return (fraction, startInside); }
            else { yMin = -a.Y / dy; yMax = (limit - a.Y) / dy; if (!(yMin <= yMax)) (yMin, yMax) = (yMax, yMin); }
            float t0 = xMin <= yMin ? yMin : xMin, t1 = xMax < yMax ? xMax : yMax;
            if (!(t0 <= 1) || t1 < 0) return (fraction, startInside);
            if (t0 < 0) t0 = 0;
            if (!(t1 <= 1)) t1 = 1;
            q = new(t1 * dx + a.X, t1 * dy + a.Y); p = new(t0 * dx + a.X, t0 * dy + a.Y);
        }
        // Retail walk: a fixed number of cells, ceil(|dx|) + ceil(|dy|) (at least one), stepping by error
        // terms that start at the distance to the next grid line. Short segments crossing both an x and a
        // y line therefore stop one cell early, as in the retail client.
        float spanX = Math.Abs(q.X - p.X), spanY = Math.Abs(q.Y - p.Y);
        int stepX = p.X < q.X ? 1 : -1, stepY = p.Y < q.Y ? 1 : -1;
        float errorX = (float)(stepX > 0 ? Math.Ceiling(p.X) - p.X : p.X - Math.Floor(p.X)) * spanY;
        float errorY = (float)(stepY > 0 ? Math.Ceiling(p.Y) - p.Y : p.Y - Math.Floor(p.Y)) * spanX;
        int cx = (int)Math.Floor(p.X), cy = (int)Math.Floor(p.Y);
        int count = (int)(Math.Ceiling(spanX) + Math.Ceiling(spanY)); if (count == 0) count = 1;
        Vector3 direction = end - start;
        for (; count > 0; count--)
        {
            if (CellHit(cx, cy, start, direction, ref fraction)) break;
            if (errorX < errorY) { cx += stepX; errorX += spanY; } else { cy += stepY; errorY += spanX; }
        }
        return (fraction, startInside);
    }

    private bool CellHit(int cx, int cy, Vector3 start, Vector3 direction, ref float fraction)
    {
        float x0 = (cx - half) * xScale, x1 = (cx + 1 - half) * xScale, y0 = (cy - half) * yScale, y1 = (cy + 1 - half) * yScale;
        int i0 = cx & mask, i1 = (cx + 1) & mask, j0 = (cy & mask) << shift, j1 = ((cy + 1) & mask) << shift;
        var v00 = new Vector3(x0, y0, heights[j0 + i0] * heightScale); var v10 = new Vector3(x1, y0, heights[j0 + i1] * heightScale);
        var v01 = new Vector3(x0, y1, heights[j1 + i0] * heightScale); var v11 = new Vector3(x1, y1, heights[j1 + i1] * heightScale);
        bool hit = Triangle(v00, v10, v11, start, direction, ref fraction);
        hit |= Triangle(v00, v11, v01, start, direction, ref fraction);
        return hit;
    }

    private static bool Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 start, Vector3 direction, ref float fraction)
    {
        var n = Vector3.Cross(b - a, c - a);
        float denominator = Vector3.Dot(n, direction);
        if (denominator == 0) return false;
        float t = -(Vector3.Dot(n, start) - Vector3.Dot(n, a)) / denominator;
        if (t < 0 || t > 1 || !(t < fraction)) return false;
        var p = start + direction * t;
        // Inside test in the vertical projection (the terrain triangles never stand vertical).
        float e0 = Edge(a, b, p), e1 = Edge(b, c, p), e2 = Edge(c, a, p);
        if (!(e0 >= 0 && e1 >= 0 && e2 >= 0) && !(e0 <= 0 && e1 <= 0 && e2 <= 0)) return false;
        fraction = t; return true;
        static float Edge(Vector3 u, Vector3 v, Vector3 p) => (v.X - u.X) * (p.Y - u.Y) - (v.Y - u.Y) * (p.X - u.X);
    }

    private void Cell(Vector2 location, out float fx, out float fy, out float h00, out float h10, out float h01, out float h11)
    {
        float gx = location.X / xScale + half, gy = location.Y / yScale + half;
        int ix = (int)Math.Floor(gx), iy = (int)Math.Floor(gy);
        fx = gx - ix; fy = gy - iy;
        int x0 = ix & mask, x1 = (ix + 1) & mask, y0 = (iy & mask) << shift, y1 = ((iy + 1) & mask) << shift;
        h00 = heights[y0 + x0] * heightScale; h10 = heights[y0 + x1] * heightScale;
        h01 = heights[y1 + x0] * heightScale; h11 = heights[y1 + x1] * heightScale;
    }

}
