using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI button symbols (the micro menu and its menus), drawn in code at their exact pixel size from simple
/// shapes on a 24-unit grid: anti-aliased, with a soft dark edge so they read on any background.
/// </summary>
internal static class EnbModernSymbols
{
    /// <summary>The symbols, by name.</summary>
    public static readonly string[] Names = ["inventory", "equipment", "skills", "missions", "factions", "map", "galaxy", "local", "multibox", "help", "options", "submenu", "back", "vault", "music", "play", "pause", "next", "previous"];
    private static readonly Dictionary<(string Name, int Size, bool Hot), EnbUiRaster> cache = [];
    private delegate float Distance(Vector2 p);

    /// <summary>The <paramref name="name"/> symbol filling a <paramref name="size"/> pixel square; brighter when hovered.</summary>
    public static EnbUiRaster? Get(string name, int size, bool hovered)
    {
        size = Math.Clamp(size, 8, 160);
        lock (cache)
        {
            if (cache.TryGetValue((name, size, hovered), out var cached)) return cached;
            var shapes = Shapes(name);
            if (shapes.Count == 0) return null;
            return cache[(name, size, hovered)] = Rasterize(shapes, size, hovered ? new(1, 1, .45f) : new(.85f, .9f, 1));
        }
    }

    private static EnbUiRaster Rasterize(List<Distance> shapes, int size, EnbRgb color)
    {
        const int samples = 4;
        float unit = 24f / size, edge = unit * 1.1f;
        var pixels = new EnbColor32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int ink = 0, rim = 0;
                for (int sy = 0; sy < samples; sy++)
                    for (int sx = 0; sx < samples; sx++)
                    {
                        var p = new Vector2((x + (sx + .5f) / samples) * unit, (y + (sy + .5f) / samples) * unit);
                        float d = float.MaxValue;
                        foreach (var shape in shapes) d = Math.Min(d, shape(p));
                        if (d <= 0) ink++; else if (d <= edge) rim++;
                    }
                float a = ink / (float)(samples * samples), r = (ink + rim) / (float)(samples * samples) * .55f;
                // The symbol over its dark edge.
                float alpha = a + r * (1 - a);
                if (alpha <= 0) continue;
                float k = a / alpha;
                pixels[y * size + x] = new((byte)(color.R * 255 * k), (byte)(color.G * 255 * k), (byte)(color.B * 255 * k), (byte)Math.Round(alpha * 255));
            }
        return EnbUiRaster.FromPixels(size, size, pixels);
    }

    // Shapes on the 24-unit grid: a negative or zero distance is inside.
    private static readonly Dictionary<(int Size, int Level), EnbUiRaster> signals = [];
    /// <summary>The connection indicator: three rising bars. Good fills all three in green, fair two in orange, poor one in red;
    /// the rest stay outlines. Level 0 (nothing measured yet) shows only the outlines.</summary>
    public static EnbUiRaster? Signal(int size, int level)
    {
        size = Math.Clamp(size, 8, 160); level = Math.Clamp(level, 0, 3);
        lock (signals)
        {
            if (signals.TryGetValue((size, level), out var cached)) return cached;
            int filled = level == 0 ? 0 : 4 - level;
            var shapes = new List<Distance>();
            for (int i = 0; i < 3; i++)
            {
                float x0 = 3.5f + i * 6.5f, x1 = x0 + 4, top = 14 - i * 5, bottom = 20.5f;
                shapes.Add(i < filled ? Fill(x0, top, x1, top, x1, bottom, x0, bottom) : Box(x0, top, x1, bottom, .6f, 1.5f));
            }
            EnbRgb color = level switch { 1 => new(.35f, 1, .35f), 2 => new(1, .62f, .2f), 3 => new(1, .25f, .2f), _ => new(.85f, .9f, 1) };
            return signals[(size, level)] = Rasterize(shapes, size, color);
        }
    }

    private static Distance Line(float ax, float ay, float bx, float by, float width) => p => SegmentDistance(p, new(ax, ay), new(bx, by)) - width / 2;
    private static Distance Path(float width, bool closed, params float[] xy)
    {
        var points = Points(xy);
        return p =>
        {
            float d = float.MaxValue;
            for (int i = 0; i + 1 < points.Length; i++) d = Math.Min(d, SegmentDistance(p, points[i], points[i + 1]));
            if (closed) d = Math.Min(d, SegmentDistance(p, points[^1], points[0]));
            return d - width / 2;
        };
    }
    private static Distance Fill(params float[] xy)
    {
        var points = Points(xy);
        return p =>
        {
            float d = float.MaxValue; bool inside = false;
            for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
            {
                var a = points[i]; var b = points[j];
                d = Math.Min(d, SegmentDistance(p, a, b));
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside ? -d : d;
        };
    }
    private static Distance Ring(float cx, float cy, float radius, float width) => p => Math.Abs(Vector2.Distance(p, new(cx, cy)) - radius) - width / 2;
    private static Distance Disc(float cx, float cy, float radius) => p => Vector2.Distance(p, new(cx, cy)) - radius;
    /// <summary>An arc from <paramref name="from"/> to <paramref name="to"/> degrees (0 = right, 90 = down).</summary>
    private static Distance Arc(float cx, float cy, float radius, float from, float to, float width)
    {
        int steps = Math.Max(4, (int)(Math.Abs(to - from) / 8));
        var xy = new float[(steps + 1) * 2];
        for (int i = 0; i <= steps; i++)
        {
            float a = (from + (to - from) * i / steps) * MathF.PI / 180;
            xy[i * 2] = cx + radius * MathF.Cos(a); xy[i * 2 + 1] = cy + radius * MathF.Sin(a);
        }
        return Path(width, false, xy);
    }
    private static Distance Box(float x0, float y0, float x1, float y1, float corner, float width)
    {
        var centre = new Vector2((x0 + x1) / 2, (y0 + y1) / 2); var half = new Vector2((x1 - x0) / 2 - corner, (y1 - y0) / 2 - corner);
        return p =>
        {
            var q = Vector2.Abs(p - centre) - half;
            float outside = Vector2.Max(q, Vector2.Zero).Length() + Math.Min(Math.Max(q.X, q.Y), 0) - corner;
            return Math.Abs(outside) - width / 2;
        };
    }
    private static Vector2[] Points(float[] xy) { var points = new Vector2[xy.Length / 2]; for (int i = 0; i < points.Length; i++) points[i] = new(xy[i * 2], xy[i * 2 + 1]); return points; }
    private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a; float t = ab.LengthSquared() <= 0 ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }

    private static List<Distance> Shapes(string name)
    {
        const float w = 2f;
        switch (name)
        {
            case "inventory": // A cargo bag: body, handle and clasp.
                return [Box(4.5f, 8.5f, 19.5f, 20.5f, 2.5f, w), Arc(12, 8.5f, 4, 180, 360, w), Line(4.5f, 13, 19.5f, 13, 1.6f), Disc(12, 13, 1.8f)];
            case "equipment": // A ship seen from above.
                return [Fill(12, 2.5f, 14.4f, 10, 21.5f, 17, 21.5f, 19.8f, 14.2f, 17.8f, 12, 21.5f, 9.8f, 17.8f, 2.5f, 19.8f, 2.5f, 17, 9.6f, 10)];
            case "skills": // A star.
            {
                var xy = new float[20];
                for (int i = 0; i < 10; i++)
                {
                    float a = (-90 + i * 36) * MathF.PI / 180, r = i % 2 == 0 ? 10 : 4.2f;
                    xy[i * 2] = 12 + r * MathF.Cos(a); xy[i * 2 + 1] = 13 + r * MathF.Sin(a);
                }
                return [Fill(xy)];
            }
            case "missions": // A checklist.
                return [Box(5, 3.5f, 19, 21, 2, w), Line(9, 9, 15.5f, 9, 1.8f), Line(9, 13, 15.5f, 13, 1.8f), Line(9, 17, 13.5f, 17, 1.8f)];
            case "factions": // A flag on its pole.
                return [Line(6, 3, 6, 21.5f, w), Fill(6, 3.5f, 19.5f, 3.5f, 16.2f, 8.5f, 19.5f, 13.5f, 6, 13.5f)];
            case "map": // A folded map.
                return [Path(1.8f, true, 3, 6, 9, 3.5f, 15, 6, 21, 3.5f, 21, 18, 15, 20.5f, 9, 18, 3, 20.5f), Line(9, 3.5f, 9, 18, 1.4f), Line(15, 6, 15, 20.5f, 1.4f)];
            case "galaxy": // A spiral galaxy: two arms round a bright core.
            {
                var shapes = new List<Distance> { Disc(12, 12, 2.4f) };
                for (int arm = 0; arm < 2; arm++)
                {
                    var xy = new float[2 * 24];
                    for (int i = 0; i < 24; i++)
                    {
                        float t = i / 23f, a = arm * MathF.PI + t * 1.75f * MathF.PI, r = 2 + 8 * t;
                        xy[i * 2] = 12 + r * MathF.Cos(a); xy[i * 2 + 1] = 12 + r * MathF.Sin(a) * .8f;
                    }
                    shapes.Add(Path(1.9f, false, xy));
                }
                return shapes;
            }
            case "local": // A radar scope: its ring, the sweep and two contacts.
                return [Ring(12, 12, 9.4f, 1.8f), Line(12, 12, 18, 6.2f, 1.8f), Disc(12, 12, 1.5f), Disc(8, 15.5f, 1.7f), Disc(15.5f, 16, 1.5f)];
            case "multibox": // Two windows, one behind the other.
                return [Box(3.5f, 8.5f, 15.5f, 20.5f, 1.5f, 1.8f), Path(1.8f, false, 8.5f, 8.5f, 8.5f, 3.5f, 20.5f, 3.5f, 20.5f, 15.5f, 15.5f, 15.5f)];
            case "social": // Two people: heads and shoulders, one behind the other.
                return [Disc(9, 8, 3.2f), Arc(9, 19.5f, 6.2f, 180, 360, w), Ring(16.5f, 7, 2.6f, 1.6f), Arc(16.5f, 17.5f, 5, 200, 340, 1.6f)];
            case "log": // A notebook: its cover, the spine's rings and a pen across it.
                return [Box(5, 3.5f, 18, 20.5f, 1.8f, w), Line(5, 7.5f, 3, 7.5f, 1.6f), Line(5, 12, 3, 12, 1.6f), Line(5, 16.5f, 3, 16.5f, 1.6f),
                    Line(9, 8.5f, 14.5f, 8.5f, 1.5f), Line(9, 12, 14.5f, 12, 1.5f), Line(21, 7, 13.5f, 18.5f, 2), Line(13.5f, 18.5f, 12.8f, 20.5f, 1.4f)];
            case "help": // A question mark in a circle.
                return [Ring(12, 12, 9.6f, 1.8f), Arc(12, 9.4f, 3.2f, 180, 405, 2.1f), Line(14.26f, 11.66f, 12, 13.6f, 2.1f), Line(12, 13.6f, 12, 14.8f, 2.1f), Disc(12, 18, 1.35f)];
            case "options": // A gear wheel.
            {
                var shapes = new List<Distance> { Ring(12, 12, 5.2f, 4.2f) };
                for (int i = 0; i < 8; i++)
                {
                    float a = i * MathF.PI / 4;
                    shapes.Add(Line(12 + 6.4f * MathF.Cos(a), 12 + 6.4f * MathF.Sin(a), 12 + 9.3f * MathF.Cos(a), 12 + 9.3f * MathF.Sin(a), 3.4f));
                }
                return shapes;
            }
            case "vault": // A safe: its body, the door's dial and handle.
                return [Box(3.5f, 4, 20.5f, 19.5f, 2, w), Ring(11, 11.75f, 3.6f, 1.8f), Line(11, 9.4f, 11, 11.75f, 1.6f), Line(17, 9, 17, 14.5f, 2), Line(6, 19.5f, 6, 21.5f, 2), Line(18, 19.5f, 18, 21.5f, 2)];
            case "items": // A catalogue: an open book, a line on each page.
                return [Path(1.8f, true, 12, 6.5f, 8, 4.5f, 3, 5, 3, 19, 8, 18.5f, 12, 20.5f, 16, 18.5f, 21, 19, 21, 5, 16, 4.5f), Line(12, 6.5f, 12, 20.5f, 1.6f),
                    Line(5.6f, 9.5f, 9.6f, 9.2f, 1.4f), Line(5.6f, 13, 9.6f, 12.8f, 1.4f), Line(14.4f, 9.2f, 18.4f, 9.5f, 1.4f), Line(14.4f, 12.8f, 18.4f, 13, 1.4f)];
            case "music": // Two notes joined by their beam.
                return [Disc(7, 18.2f, 2.9f), Disc(17, 16.2f, 2.9f), Line(9.4f, 18, 9.4f, 5.6f, 1.8f), Line(19.4f, 16, 19.4f, 3.6f, 1.8f), Line(9.4f, 5.6f, 19.4f, 3.6f, 2.8f)];
            case "play": // Play: a triangle pointing right.
                return [Fill(7, 4.5f, 19.5f, 12, 7, 19.5f)];
            case "pause": // Pause: two bars.
                return [Fill(6, 5, 10, 5, 10, 19, 6, 19), Fill(14, 5, 18, 5, 18, 19, 14, 19)];
            case "next": // Next: a triangle to a bar.
                return [Fill(4.5f, 5, 14.5f, 12, 4.5f, 19), Fill(15.5f, 5, 19, 5, 19, 19, 15.5f, 19)];
            case "previous": // Previous: a bar and a triangle pointing left.
                return [Fill(19.5f, 5, 9.5f, 12, 19.5f, 19), Fill(5, 5, 8.5f, 5, 8.5f, 19, 5, 19)];
            case "submenu": // The arrow of a menu entry that opens another menu.
                return [Path(2.2f, false, 9, 6, 15, 12, 9, 18)];
            case "back": // The same arrow, pointing left (a slider's step down).
                return [Path(2.2f, false, 15, 6, 9, 12, 15, 18)];
            default: return [];
        }
    }
}
