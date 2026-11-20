using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

/// <summary>
/// Software-rendered 3D waypoint arrow: a shaded cylinder shaft with a cone head, seen from
/// slightly behind and above the ship. Bearing turns it left/right, elevation tips it toward or
/// away from the viewer, so "above", "ahead" and "below" read differently. Coloured green
/// (ahead) through yellow to red (behind), with a soft drop shadow instead of a hard rim.
/// </summary>
internal static class EnbWaypointArrowRaster
{
    private const int Size = 64, Super = 3, Big = Size * Super, CacheLimit = 256;
    private const float ViewElevation = 55 * MathF.PI / 180;
    private static readonly Dictionary<(int, int), EnbUiRaster> Cache = [];
    private static readonly (Vector3 A, Vector3 B, Vector3 C)[] Mesh = BuildMesh();

    /// <summary>yaw: radians counterclockwise (left) of the nose; pitch: radians above the nose.</summary>
    public static EnbUiRaster Get(float yaw, float pitch)
    {
        var key = ((int)MathF.Round(yaw * 1000), (int)MathF.Round(pitch * 1000));
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var raster)) return raster;
            if (Cache.Count >= CacheLimit) Cache.Clear();
            return Cache[key] = Build(yaw, pitch);
        }
    }

    private static (Vector3, Vector3, Vector3)[] BuildMesh()
    {
        const int segments = 18;
        const float tail = -.92f, neck = .18f, tip = 1f, shaft = .13f, head = .36f;
        var triangles = new List<(Vector3, Vector3, Vector3)>();
        Vector3 Ring(float y, float r, int i) { float a = i * MathF.Tau / segments; return new(MathF.Cos(a) * r, y, MathF.Sin(a) * r); }
        for (int i = 0; i < segments; i++)
        {
            int j = i + 1;
            triangles.Add((Ring(tail, shaft, i), Ring(neck, shaft, i), Ring(neck, shaft, j)));
            triangles.Add((Ring(tail, shaft, i), Ring(neck, shaft, j), Ring(tail, shaft, j)));
            triangles.Add((new(0, tail, 0), Ring(tail, shaft, j), Ring(tail, shaft, i)));
            triangles.Add((Ring(neck, shaft, i), Ring(neck, head, i), Ring(neck, head, j)));
            triangles.Add((Ring(neck, shaft, i), Ring(neck, head, j), Ring(neck, shaft, j)));
            triangles.Add((Ring(neck, head, i), new(0, tip, 0), Ring(neck, head, j)));
        }
        return [.. triangles];
    }

    private static EnbUiRaster Build(float yaw, float pitch)
    {
        // Arrow axis in ship space (x right, y forward, z up); the mesh is symmetric about it.
        var axis = new Vector3(-MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(pitch));
        var side = MathF.Abs(axis.Z) > .95f ? Vector3.UnitX : Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitZ));
        var normal = Vector3.Cross(side, axis);
        float sinV = MathF.Sin(ViewElevation), cosV = MathF.Cos(ViewElevation);
        // View: right = x; up = y sin + z cos; toward the viewer = -y cos + z sin.
        Vector3 View(Vector3 m)
        {
            var w = side * m.X + axis * m.Y + normal * m.Z;
            return new(w.X, w.Y * sinV + w.Z * cosV, -w.Y * cosV + w.Z * sinV);
        }
        float off = MathF.Acos(Math.Clamp(MathF.Cos(yaw) * MathF.Cos(pitch), -1, 1)) / MathF.PI;
        Vector3 fill = off < .5f ? Vector3.Lerp(new(.3f, 1, .3f), new(1, .92f, .2f), off * 2) : Vector3.Lerp(new(1, .92f, .2f), new(1, .28f, .22f), (off - .5f) * 2);
        var light = Vector3.Normalize(new(-.45f, .6f, .75f));
        var depth = new float[Big * Big]; Array.Fill(depth, float.NegativeInfinity);
        var shade = new float[Big * Big];
        float scale = Big / 2.3f;
        foreach (var (ma, mb, mc) in Mesh)
        {
            Vector3 a = View(ma), b = View(mb), c = View(mc);
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a)); if (n.Z < 0) n = -n;
            float intensity = .38f + .62f * MathF.Max(0, Vector3.Dot(n, light)) + .25f * MathF.Pow(MathF.Max(0, Vector3.Dot(Vector3.Normalize(light + Vector3.UnitZ), n)), 24);
            Vector2 P(Vector3 v) => new(Big / 2f + v.X * scale, Big / 2f - v.Y * scale);
            Vector2 pa = P(a), pb = P(b), pc = P(c);
            float area = (pb.X - pa.X) * (pc.Y - pa.Y) - (pb.Y - pa.Y) * (pc.X - pa.X);
            if (MathF.Abs(area) < 1e-6f) continue;
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.X, MathF.Min(pb.X, pc.X)))), x1 = Math.Min(Big - 1, (int)MathF.Ceiling(MathF.Max(pa.X, MathF.Max(pb.X, pc.X))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y)))), y1 = Math.Min(Big - 1, (int)MathF.Ceiling(MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y))));
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float px = x + .5f, py = y + .5f;
                float w0 = ((pb.X - px) * (pc.Y - py) - (pb.Y - py) * (pc.X - px)) / area;
                float w1 = ((pc.X - px) * (pa.Y - py) - (pc.Y - py) * (pa.X - px)) / area;
                float w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                float z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                int index = y * Big + x;
                if (z <= depth[index]) continue;
                depth[index] = z; shade[index] = Math.Min(1.15f, intensity);
            }
        }
        var pixels = new EnbColor32[Size * Size]; var coverage = new float[Size * Size];
        var colours = new Vector3[Size * Size];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            int count = 0; float sum = 0;
            for (int sy = 0; sy < Super; sy++)
            for (int sx = 0; sx < Super; sx++)
            {
                int index = (y * Super + sy) * Big + x * Super + sx;
                if (float.IsNegativeInfinity(depth[index])) continue;
                count++; sum += shade[index];
            }
            if (count == 0) continue;
            coverage[y * Size + x] = count / (float)(Super * Super);
            colours[y * Size + x] = Vector3.Min(Vector3.One, fill * (sum / count));
        }
        // Soft drop shadow: a blurred, offset copy of the coverage, well under the arrow's opacity.
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            float shadow = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int sx = x - 1 + dx, sy = y - 1 + dy;
                if (sx >= 0 && sy >= 0 && sx < Size && sy < Size) shadow += coverage[sy * Size + sx];
            }
            shadow = shadow / 9 * .5f;
            float a = coverage[y * Size + x], outA = a + shadow * (1 - a);
            if (outA <= 0) continue;
            var c = colours[y * Size + x] * a / outA;
            pixels[y * Size + x] = new((byte)(c.X * 255), (byte)(c.Y * 255), (byte)(c.Z * 255), (byte)(Math.Min(1, outA) * 255));
        }
        return EnbUiRaster.FromPixels(Size, Size, pixels);
    }
}
