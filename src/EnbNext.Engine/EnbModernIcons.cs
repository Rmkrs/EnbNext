using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

/// <summary>
/// Small Modern UI symbols drawn in code at their exact pixel size (no font glyphs, no scaling), so they stay
/// crisp at any resolution: an anti-aliased stroke with a one-pixel soft dark edge for contrast.
/// </summary>
internal static class EnbModernIcons
{
    // One cache for every drawn symbol: kind, size and colour.
    private static readonly Dictionary<(int Kind, int Width, int Height, EnbRgb Color), EnbUiRaster> cache = [];

    /// <summary>A close cross filling a <paramref name="size"/> pixel square.</summary>
    public static EnbUiRaster Cross(int size, EnbRgb color)
    {
        size = Math.Clamp(size, 6, 128);
        lock (cache)
        {
            if (cache.TryGetValue((0, size, size, color), out var cached)) return cached;
            var pixels = new EnbColor32[size * size];
            float margin = size * .31f, half = Math.Max(.75f, size * .07f), end = size - margin;
            byte r = (byte)(Math.Clamp(color.R, 0, 1) * 255), g = (byte)(Math.Clamp(color.G, 0, 1) * 255), b = (byte)(Math.Clamp(color.B, 0, 1) * 255);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float px = x + .5f, py = y + .5f;
                    float d = Math.Min(Segment(px, py, margin, margin, end, end), Segment(px, py, end, margin, margin, end));
                    float fill = Math.Clamp(half + .5f - d, 0, 1), edge = Math.Clamp(half + 1.5f - d, 0, 1) * .55f;
                    float alpha = fill + edge * (1 - fill);
                    if (alpha <= 0) continue;
                    float k = fill / alpha;
                    pixels[y * size + x] = new((byte)(r * k), (byte)(g * k), (byte)(b * k), (byte)(alpha * 255));
                }
            return cache[(0, size, size, color)] = EnbUiRaster.FromPixels(size, size, pixels);
        }
    }


    /// <summary>
    /// The Modern UI close button, complete: a rounded square in the Modern UI frame colours (a dark edge, a
    /// lighter rim, a top-to-bottom shade; the highlight colour on hover) with the cross on top. The game's
    /// stretchable dialog frames are made for panels; squeezed to a button this small their pieces show through.
    /// </summary>
    public static EnbUiRaster CloseButton(int size, bool hovered)
    {
        size = Math.Clamp(size, 10, 128);
        lock (cache)
        {
            if (cache.TryGetValue((1, size, size, new(hovered ? 1 : 0, 0, 0)), out var cached)) return cached;
            var cross = Cross(size, hovered ? new(1, 1, .3f) : new(.88f, .92f, 1));
            var pixels = new EnbColor32[size * size];
            float radius = Math.Max(2, size * .2f), c = size / 2f;
            (float R, float G, float B) top = hovered ? (64, 60, 128) : (36, 49, 92), bottom = hovered ? (42, 38, 90) : (20, 28, 58),
                rim = hovered ? (110, 104, 190) : (58, 72, 120), edge = (2, 3, 6);
            var crossBytes = cross.BgraPixels.AsSpan();
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // Signed distance to the rounded square (negative inside).
                    float qx = Math.Abs(x + .5f - c) - (c - radius), qy = Math.Abs(y + .5f - c) - (c - radius);
                    float outside = MathF.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - radius;
                    float coverage = Math.Clamp(.5f - outside, 0, 1);
                    if (coverage <= 0) continue;
                    float t = (y + .5f) / size;
                    float r = top.R + (bottom.R - top.R) * t, g = top.G + (bottom.G - top.G) * t, bl = top.B + (bottom.B - top.B) * t;
                    float depth = -outside;
                    // One-pixel dark edge, then a one-pixel rim, brighter towards the top.
                    float edgeMix = Math.Clamp(1.5f - depth, 0, 1), rimMix = Math.Clamp(2.5f - depth, 0, 1) * (1 - edgeMix) * (1.1f - t * .8f);
                    r += (rim.R - r) * rimMix; g += (rim.G - g) * rimMix; bl += (rim.B - bl) * rimMix;
                    r += (edge.Item1 - r) * edgeMix; g += (edge.Item2 - g) * edgeMix; bl += (edge.Item3 - bl) * edgeMix;
                    // The cross over it (its pixels are straight BGRA).
                    int i = (y * size + x) * 4;
                    float a = crossBytes[i + 3] / 255f;
                    r += (crossBytes[i + 2] - r) * a; g += (crossBytes[i + 1] - g) * a; bl += (crossBytes[i] - bl) * a;
                    pixels[y * size + x] = new((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(bl, 0, 255), (byte)(coverage * 255));
                }
            return cache[(1, size, size, new(hovered ? 1 : 0, 0, 0))] = EnbUiRaster.FromPixels(size, size, pixels);
        }
    }


    /// <summary>
    /// A Modern UI push button's face at its exact pixel size: the close button's rounded panel (dark edge, lighter
    /// rim, top-to-bottom shade; the highlight colours on hover), widened to fit a label. Drawn rather than built
    /// from the game's stretchable dialog frames, whose pieces show through at button sizes.
    /// </summary>
    public static EnbUiRaster Button(int width, int height, bool hovered)
    {
        width = Math.Clamp(width, 10, 1024); height = Math.Clamp(height, 10, 256);
        lock (cache)
        {
            if (cache.TryGetValue((6, width, height, new(hovered ? 1 : 0, 0, 0)), out var cached)) return cached;
            var pixels = new EnbColor32[width * height];
            float radius = Math.Max(2, height * .2f), cx = width / 2f, cy = height / 2f;
            (float R, float G, float B) top = hovered ? (64, 60, 128) : (36, 49, 92), bottom = hovered ? (42, 38, 90) : (20, 28, 58),
                rim = hovered ? (110, 104, 190) : (58, 72, 120), edge = (2, 3, 6);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float qx = Math.Abs(x + .5f - cx) - (cx - radius), qy = Math.Abs(y + .5f - cy) - (cy - radius);
                    float outside = MathF.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - radius;
                    float coverage = Math.Clamp(.5f - outside, 0, 1);
                    if (coverage <= 0) continue;
                    float t = (y + .5f) / height;
                    float r = top.R + (bottom.R - top.R) * t, g = top.G + (bottom.G - top.G) * t, b = top.B + (bottom.B - top.B) * t;
                    float depth = -outside;
                    float edgeMix = Math.Clamp(1.5f - depth, 0, 1), rimMix = Math.Clamp(2.5f - depth, 0, 1) * (1 - edgeMix) * (1.1f - t * .8f);
                    r += (rim.R - r) * rimMix; g += (rim.G - g) * rimMix; b += (rim.B - b) * rimMix;
                    r += (edge.Item1 - r) * edgeMix; g += (edge.Item2 - g) * edgeMix; b += (edge.Item3 - b) * edgeMix;
                    pixels[y * width + x] = new((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), (byte)(coverage * 255));
                }
            return cache[(6, width, height, new(hovered ? 1 : 0, 0, 0))] = EnbUiRaster.FromPixels(width, height, pixels);
        }
    }

    /// <summary>A hovered slot: a soft light-blue rounded border with a faint inner glow, drawn over the slot.</summary>
    public static EnbUiRaster SlotHighlight(int width, int height)
    {
        width = Math.Clamp(width, 8, 256); height = Math.Clamp(height, 8, 256);
        lock (cache)
        {
            if (cache.TryGetValue((2, width, height, default), out var cached)) return cached;
            var pixels = new EnbColor32[width * height];
            float radius = Math.Max(2, Math.Min(width, height) * .1f), cx = width / 2f, cy = height / 2f;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float qx = Math.Abs(x + .5f - cx) - (cx - radius), qy = Math.Abs(y + .5f - cy) - (cy - radius);
                    float outside = MathF.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - radius;
                    float depth = -outside;
                    if (depth < -.5f) continue;
                    float border = Math.Clamp(1 - Math.Abs(depth - 1.25f) / 1.25f, 0, 1), glow = Math.Clamp(1 - depth / (Math.Min(width, height) * .28f), 0, 1) * .22f;
                    float alpha = Math.Max(border * .95f, glow);
                    if (alpha <= 0) continue;
                    pixels[y * width + x] = new(150, 205, 255, (byte)(alpha * 255));
                }
            return cache[(2, width, height, default)] = EnbUiRaster.FromPixels(width, height, pixels);
        }
    }

    /// <summary>A slim pill (fully rounded ends) in a flat colour with soft edges: a scroll bar's track or thumb.</summary>
    public static EnbUiRaster Pill(int width, int height, EnbRgb color, byte alpha)
    {
        width = Math.Clamp(width, 2, 64); height = Math.Clamp(height, 2, 4096);
        var key = (100 + alpha, width, height, color);
        lock (cache)
        {
            if (cache.TryGetValue(key, out var cached)) return cached;
            var pixels = new EnbColor32[width * height];
            float radius = Math.Min(width, height) / 2f, cx = width / 2f, cy = height / 2f;
            byte r = (byte)(Math.Clamp(color.R, 0, 1) * 255), g = (byte)(Math.Clamp(color.G, 0, 1) * 255), b = (byte)(Math.Clamp(color.B, 0, 1) * 255);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float qx = Math.Abs(x + .5f - cx) - (cx - radius), qy = Math.Abs(y + .5f - cy) - (cy - radius);
                    float outside = MathF.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - radius;
                    float coverage = Math.Clamp(.5f - outside, 0, 1);
                    if (coverage > 0) pixels[y * width + x] = new(r, g, b, (byte)(alpha * coverage));
                }
            return cache[key] = EnbUiRaster.FromPixels(width, height, pixels);
        }
    }

    /// <summary>A flat colour with the given opacity, stretched over a rectangle (row shading).</summary>
    public static EnbUiRaster Fill(EnbRgb color, byte alpha)
    {
        lock (cache)
        {
            if (cache.TryGetValue((5, alpha, 1, color), out var cached)) return cached;
            var pixel = new EnbColor32((byte)(Math.Clamp(color.R, 0, 1) * 255), (byte)(Math.Clamp(color.G, 0, 1) * 255), (byte)(Math.Clamp(color.B, 0, 1) * 255), alpha);
            return cache[(5, alpha, 1, color)] = EnbUiRaster.FromPixels(1, 1, [pixel]);
        }
    }

    /// <summary>A skill rank pip: a gold disc for a trained rank, an empty dark ring for one still to train.</summary>
    public static EnbUiRaster Pip(int size, bool filled)
    {
        size = Math.Clamp(size, 4, 64);
        lock (cache)
        {
            if (cache.TryGetValue((3, size, size, new(filled ? 1 : 0, 0, 0)), out var cached)) return cached;
            var pixels = new EnbColor32[size * size];
            float c = size / 2f, radius = c - .25f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x + .5f - c, dy = y + .5f - c, d = MathF.Sqrt(dx * dx + dy * dy), depth = radius - d;
                    float coverage = Math.Clamp(depth + .5f, 0, 1);
                    if (coverage <= 0) continue;
                    float r, g, b;
                    if (filled)
                    {
                        // Lit from the top left: a pale gold highlight into a deeper gold.
                        float light = Math.Clamp(1 - MathF.Sqrt((dx + c * .35f) * (dx + c * .35f) + (dy + c * .35f) * (dy + c * .35f)) / (size * .85f), 0, 1);
                        r = 168 + 87 * light; g = 132 + 104 * light; b = 46 + 104 * light * light;
                    }
                    else { r = 12; g = 16; b = 30; }
                    // A one-pixel rim (lighter on an empty pip) inside a soft dark edge.
                    float rimMix = Math.Clamp(1.6f - depth, 0, 1), edgeMix = Math.Clamp(.8f - depth, 0, 1);
                    (float R, float G, float B) rim = filled ? (120, 92, 30) : (104, 116, 150);
                    r += (rim.R - r) * rimMix; g += (rim.G - g) * rimMix; b += (rim.B - b) * rimMix;
                    r += (4 - r) * edgeMix * .6f; g += (5 - g) * edgeMix * .6f; b += (8 - b) * edgeMix * .6f;
                    pixels[y * size + x] = new((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), (byte)(coverage * 255));
                }
            return cache[(3, size, size, new(filled ? 1 : 0, 0, 0))] = EnbUiRaster.FromPixels(size, size, pixels);
        }
    }

    /// <summary>A map marker: a disc in the given colour with a dark rim (the player's own one carries a white ring).</summary>
    public static EnbUiRaster Dot(int size, EnbRgb color, bool ringed = false)
    {
        size = Math.Clamp(size, 4, 64);
        lock (cache)
        {
            var key = (ringed ? 8 : 7, size, size, color);
            if (cache.TryGetValue(key, out var cached)) return cached;
            var pixels = new EnbColor32[size * size];
            float c = size / 2f, radius = c - .25f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x + .5f - c, dy = y + .5f - c, depth = radius - MathF.Sqrt(dx * dx + dy * dy);
                    float coverage = Math.Clamp(depth + .5f, 0, 1);
                    if (coverage <= 0) continue;
                    float r = color.R * 255, g = color.G * 255, b = color.B * 255;
                    float rim = ringed ? Math.Clamp(2.6f - depth, 0, 1) : 0, edge = Math.Clamp(1.2f - depth, 0, 1);
                    r += (255 - r) * rim; g += (255 - g) * rim; b += (255 - b) * rim;
                    r *= 1 - edge * .7f; g *= 1 - edge * .7f; b *= 1 - edge * .7f;
                    pixels[y * size + x] = new((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), (byte)(coverage * 255));
                }
            return cache[key] = EnbUiRaster.FromPixels(size, size, pixels);
        }
    }

    /// <summary>The skill-up marker: a green upward triangle with a dark edge, brighter when hovered.</summary>
    public static EnbUiRaster UpArrow(int size, bool hovered)
    {
        size = Math.Clamp(size, 6, 64);
        lock (cache)
        {
            if (cache.TryGetValue((4, size, size, new(hovered ? 1 : 0, 0, 0)), out var cached)) return cached;
            var pixels = new EnbColor32[size * size];
            // Triangle corners, inset so the anti-aliased edge stays inside the square.
            float m = size * .14f, ax = size / 2f, ay = m, bx = size - m, by = size - m * 1.3f, cx = m, cy = by;
            (float R, float G, float B) fill = hovered ? (150, 255, 140) : (60, 205, 75), top = hovered ? (210, 255, 200) : (120, 240, 120);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float px = x + .5f, py = y + .5f;
                    // Signed distance: positive inside, the smallest distance to the three edges.
                    float e1 = Edge(px, py, ax, ay, bx, by), e2 = Edge(px, py, bx, by, cx, cy), e3 = Edge(px, py, cx, cy, ax, ay);
                    float depth = Math.Min(e1, Math.Min(e2, e3));
                    float coverage = Math.Clamp(depth + 1.5f, 0, 1);
                    if (coverage <= 0) continue;
                    float t = Math.Clamp((py - ay) / (by - ay), 0, 1);
                    float r = top.R + (fill.R - top.R) * t, g = top.G + (fill.G - top.G) * t, b = top.B + (fill.B - top.B) * t;
                    float edgeMix = Math.Clamp(1 - depth, 0, 1);
                    r += (6 - r) * edgeMix; g += (20 - g) * edgeMix; b += (8 - b) * edgeMix;
                    pixels[y * size + x] = new((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), (byte)(coverage * 255));
                }
            return cache[(4, size, size, new(hovered ? 1 : 0, 0, 0))] = EnbUiRaster.FromPixels(size, size, pixels);
        }
    }
    /// <summary>
    /// A Modern UI check box (or, <paramref name="round"/>, an option button): the button's dark rounded panel with a
    /// light rim (the highlight rim on hover), a green tick (a green dot) when on.
    /// </summary>
    public static EnbUiRaster Check(int size, bool on, bool hovered, bool round = false)
    {
        size = Math.Clamp(size, 8, 64);
        var key = (round ? 8 : 7, size, size, new EnbRgb(on ? 1 : 0, hovered ? 1 : 0, 0));
        lock (cache)
        {
            if (cache.TryGetValue(key, out var cached)) return cached;
            var pixels = new EnbColor32[size * size];
            float c = size / 2f, radius = round ? c - .25f : Math.Max(2, size * .22f);
            (float R, float G, float B) top = (30, 40, 78), bottom = (14, 20, 44), rim = hovered ? (150, 170, 255) : (88, 104, 160), mark = (90, 235, 110);
            float m = size * .26f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float px = x + .5f, py = y + .5f, outside;
                    if (round) outside = MathF.Sqrt((px - c) * (px - c) + (py - c) * (py - c)) - radius;
                    else
                    {
                        float qx = Math.Abs(px - c) - (c - radius), qy = Math.Abs(py - c) - (c - radius);
                        outside = MathF.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - radius;
                    }
                    float coverage = Math.Clamp(.5f - outside, 0, 1);
                    if (coverage <= 0) continue;
                    float t = py / size, depth = -outside;
                    float r = top.R + (bottom.R - top.R) * t, g = top.G + (bottom.G - top.G) * t, b = top.B + (bottom.B - top.B) * t;
                    float rimMix = Math.Clamp(2f - depth, 0, 1);
                    r += (rim.R - r) * rimMix; g += (rim.G - g) * rimMix; b += (rim.B - b) * rimMix;
                    if (on)
                    {
                        // The tick: two strokes; the option button: a dot.
                        float d = round ? MathF.Sqrt((px - c) * (px - c) + (py - c) * (py - c)) - size * .2f
                            : Math.Min(Segment(px, py, m, c, c * .85f, size - m * 1.05f), Segment(px, py, c * .85f, size - m * 1.05f, size - m * .9f, m)) - Math.Max(.9f, size * .08f);
                        float a = Math.Clamp(.5f - d, 0, 1);
                        r += (mark.R - r) * a; g += (mark.G - g) * a; b += (mark.B - b) * a;
                    }
                    pixels[y * size + x] = new((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), (byte)(coverage * 255));
                }
            return cache[key] = EnbUiRaster.FromPixels(size, size, pixels);
        }
    }
    /// <summary>A window's resize grip: three short diagonal strokes in the corner, brighter when hovered.</summary>
    /// <summary>A thin cyan ring (the map's find pulse), <paramref name="thickness"/> pixels wide.</summary>
    public static EnbUiRaster Ring(int size, float thickness)
    {
        size = Math.Clamp(size, 8, 256);
        var key = (10, size, (int)Math.Round(thickness * 4), new EnbRgb(0, 0, 0));
        lock (cache)
        {
            if (cache.TryGetValue(key, out var cached)) return cached;
            var pixels = new EnbColor32[size * size];
            float c = size / 2f, radius = c - thickness / 2 - 1, half = thickness / 2;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x + .5f - c, dy = y + .5f - c, d = Math.Abs(MathF.Sqrt(dx * dx + dy * dy) - radius);
                    float alpha = Math.Clamp(half + .5f - d, 0, 1);
                    if (alpha > 0) pixels[y * size + x] = new(110, 235, 255, (byte)(alpha * 255));
                }
            return cache[key] = EnbUiRaster.FromPixels(size, size, pixels);
        }
    }

    public static EnbUiRaster Grip(int size, bool hovered)
    {
        size = Math.Clamp(size, 8, 64);
        var key = (9, size, size, new EnbRgb(hovered ? 1 : 0, 0, 0));
        lock (cache)
        {
            if (cache.TryGetValue(key, out var cached)) return cached;
            var pixels = new EnbColor32[size * size];
            byte r = (byte)(hovered ? 255 : 150), g = (byte)(hovered ? 255 : 170), b = (byte)(hovered ? 120 : 220);
            float half = Math.Max(.6f, size * .05f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float px = x + .5f, py = y + .5f, d = float.MaxValue;
                    for (int n = 1; n <= 3; n++)
                    {
                        float k = size * (.28f * n);
                        d = Math.Min(d, Segment(px, py, size - 1.5f, size - k, size - k, size - 1.5f));
                    }
                    float alpha = Math.Clamp(half + .5f - d, 0, 1);
                    if (alpha > 0) pixels[y * size + x] = new(r, g, b, (byte)(alpha * 255));
                }
            return cache[key] = EnbUiRaster.FromPixels(size, size, pixels);
        }
    }

    /// <summary>
    /// A Modern UI tooltip's panel at its exact pixel size: a dark blue rounded panel, lighter at the top, with a thin
    /// light rim. Not cached: tooltips come in every size.
    /// </summary>
    public static EnbUiRaster TooltipPanel(int width, int height)
    {
        width = Math.Clamp(width, 8, 4096); height = Math.Clamp(height, 8, 4096);
        var pixels = new EnbColor32[width * height];
        float radius = Math.Clamp(height * .12f, 3, 7), cx = width / 2f, cy = height / 2f;
        (float R, float G, float B) top = (30, 40, 78), bottom = (14, 19, 42), rim = (96, 118, 186), edge = (4, 6, 12);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float qx = Math.Abs(x + .5f - cx) - (cx - radius), qy = Math.Abs(y + .5f - cy) - (cy - radius);
                float outside = MathF.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - radius;
                float coverage = Math.Clamp(.5f - outside, 0, 1);
                if (coverage <= 0) continue;
                float t = Math.Clamp((y + .5f) / Math.Min(height, 48f), 0, 1);
                float r = top.R + (bottom.R - top.R) * t, g = top.G + (bottom.G - top.G) * t, b = top.B + (bottom.B - top.B) * t;
                float depth = -outside, edgeMix = Math.Clamp(1.2f - depth, 0, 1), rimMix = Math.Clamp(2.2f - depth, 0, 1) * (1 - edgeMix);
                r += (rim.R - r) * rimMix; g += (rim.G - g) * rimMix; b += (rim.B - b) * rimMix;
                r += (edge.Item1 - r) * edgeMix; g += (edge.Item2 - g) * edgeMix; b += (edge.Item3 - b) * edgeMix;
                pixels[y * width + x] = new((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255), (byte)(coverage * 240));
            }
        return EnbUiRaster.FromPixels(width, height, pixels);
    }

    /// <summary>A dropdown's arrow: a light chevron pointing down (up while its list is open), the highlight colour on hover.</summary>
    public static EnbUiRaster Chevron(int size, bool hovered, bool up)
    {
        size = Math.Clamp(size, 6, 64);
        var key = (11, size, size, new EnbRgb(hovered ? 1 : 0, up ? 1 : 0, 0));
        lock (cache)
        {
            if (cache.TryGetValue(key, out var cached)) return cached;
            var pixels = new EnbColor32[size * size];
            byte r = (byte)(hovered ? 255 : 170), g = (byte)(hovered ? 255 : 195), b = (byte)(hovered ? 120 : 240);
            float half = Math.Max(.75f, size * .09f), side = size * .2f, top = size * .34f, bottom = size * .66f, middle = size / 2f;
            if (up) (top, bottom) = (bottom, top);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float px = x + .5f, py = y + .5f;
                    float d = Math.Min(Segment(px, py, side, top, middle, bottom), Segment(px, py, middle, bottom, size - side, top));
                    float alpha = Math.Clamp(half + .5f - d, 0, 1);
                    if (alpha > 0) pixels[y * size + x] = new(r, g, b, (byte)(alpha * 255));
                }
            return cache[key] = EnbUiRaster.FromPixels(size, size, pixels);
        }
    }

    // Distance from a point to the line through a and b, positive on the triangle's inside (clockwise corners in screen space).
    private static float Edge(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay, length = MathF.Sqrt(dx * dx + dy * dy);
        return ((px - ax) * dy - (py - ay) * dx) / length * -1;
    }

    private static float Segment(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay, t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy), 0, 1);
        float cx = ax + t * dx - px, cy = ay + t * dy - py;
        return MathF.Sqrt(cx * cx + cy * cy);
    }
}
