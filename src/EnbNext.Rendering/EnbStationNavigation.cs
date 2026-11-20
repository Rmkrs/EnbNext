using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>
/// Next station walking grid (retail has no click-to-walk): one room's floor on a one-unit
/// grid, from its collision surfaces that face up or down. A cell is walkable where there is floor and nothing stands on
/// it below head height; each walkable cell knows how far it is from the nearest wall or obstacle, so a path can keep to
/// the middle of halls and doorways. The station map draws the same cells. Retail movement never uses this.
/// </summary>
public sealed class EnbStationNavigation
{
    /// <summary>Cell size in room units.</summary>
    public const float Cell = 1;
    // What movement collides with between these heights over the floor blocks walking (a wall, a counter, a terminal);
    // anything lower is a step.
    private const float ObstacleLow = 1.3f, ObstacleHigh = 6.5f;
    // A step between neighbouring cells the avatar climbs.
    private const float Step = 1.25f;
    // The avatar's half width: the path keeps at least this far from walls.
    public const float Radius = 2.5f;
    // Clearance up to this distance still costs extra: the middle of a hall is preferred.
    private const float Middle = 6;

    public Vector2 Origin { get; }
    public int Width { get; }
    public int Height { get; }
    /// <summary>Floor height per cell (NaN: no floor).</summary>
    private readonly float[] _floor;
    /// <summary>Distance (units) from each cell to the nearest unwalkable cell; zero for unwalkable cells.</summary>
    private readonly float[] _clearance;
    /// <summary>Cells where something stands on the floor below head height.</summary>
    private readonly bool[] _blocked;
    /// <summary>The walking level (the doors stand on it).</summary>
    public float Level { get; }

    private EnbStationNavigation(Vector2 origin, int width, int height, float[] floor, bool[] blocked, float[] clearance, float level)
    { Origin = origin; Width = width; Height = height; _floor = floor; _blocked = blocked; _clearance = clearance; Level = level; }

    public float FloorAt(int x, int y) => _floor[y * Width + x];
    public bool Walkable(int x, int y) => _clearance[y * Width + x] > 0;
    public bool Obstructed(int x, int y) => _blocked[y * Width + x];
    public float ClearanceAt(int x, int y) => _clearance[y * Width + x];
    public Vector2 CellCenter(int x, int y) => Origin + new Vector2((x + .5f) * Cell, (y + .5f) * Cell);
    public bool TryCell(Vector2 at, out int x, out int y)
    {
        x = (int)MathF.Floor((at.X - Origin.X) / Cell); y = (int)MathF.Floor((at.Y - Origin.Y) / Cell);
        return x >= 0 && y >= 0 && x < Width && y < Height;
    }

    /// <summary>
    /// Builds the grid from a room's collision triangles (its doors and fixtures included); <paramref name="level"/> is
    /// its walking height. The floor is the lowest up- or down-facing surface under each cell; anything movement
    /// collides with (walls, door frames, terminals, booths, counters) between a step and head height above that floor
    /// blocks the cell.
    /// </summary>
    public static EnbStationNavigation Build(IReadOnlyList<(Vector3 A, Vector3 B, Vector3 C, bool Physical)> surfaces, float level)
    {
        var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
        bool any = false;
        foreach (var (a, b, c, _) in surfaces)
        {
            if (!IsFlat(a, b, c)) continue;
            any = true;
            min = Vector2.Min(min, Vector2.Min(new(a.X, a.Y), Vector2.Min(new(b.X, b.Y), new(c.X, c.Y))));
            max = Vector2.Max(max, Vector2.Max(new(a.X, a.Y), Vector2.Max(new(b.X, b.Y), new(c.X, c.Y))));
        }
        if (!any) throw new InvalidDataException("A station room without floor surfaces.");
        min -= new Vector2(2 * Cell); max += new Vector2(2 * Cell);
        int width = Math.Clamp((int)MathF.Ceiling((max.X - min.X) / Cell), 1, 2048), height = Math.Clamp((int)MathF.Ceiling((max.Y - min.Y) / Cell), 1, 2048);
        var floor = new float[width * height];
        Array.Fill(floor, float.NaN);
        // A cell's floor is the highest flat surface under its centre at or below a step over the walking level (a
        // bridge over lower ground, not the ground under it; not a counter top), else the lowest one there.
        float top = level + 2 * Step;
        var lowest = new float[width * height];
        Array.Fill(lowest, float.NaN);
        foreach (var (a, b, c, _) in surfaces)
        {
            if (!IsFlat(a, b, c)) continue;
            float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            int x0 = Math.Max(0, (int)MathF.Floor((MathF.Min(a.X, MathF.Min(b.X, c.X)) - min.X) / Cell));
            int x1 = Math.Min(width - 1, (int)MathF.Floor((MathF.Max(a.X, MathF.Max(b.X, c.X)) - min.X) / Cell));
            int y0 = Math.Max(0, (int)MathF.Floor((MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - min.Y) / Cell));
            int y1 = Math.Min(height - 1, (int)MathF.Floor((MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) - min.Y) / Cell));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = min.X + (x + .5f) * Cell, py = min.Y + (y + .5f) * Cell;
                    float w0 = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) / area;
                    float w1 = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < -.001f || w1 < -.001f || w2 < -.001f) continue;
                    float z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                    int i = y * width + x;
                    if (z <= top && !(z <= floor[i])) floor[i] = z;
                    if (!(z >= lowest[i])) lowest[i] = z;
                }
        }
        for (int i = 0; i < floor.Length; i++) if (float.IsNaN(floor[i])) floor[i] = lowest[i];
        // Floors meeting with a hairline gap (a room's floor and a door's): a gap of up to two cells between floors of
        // nearly one height is floor too.
        var seams = (float[])floor.Clone();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (!float.IsNaN(floor[y * width + x])) continue;
                foreach (var (dx, dy) in (ReadOnlySpan<(int, int)>)[(1, 0), (0, 1)])
                {
                    float before = float.NaN, after = float.NaN;
                    for (int k = 1; k <= 2 && float.IsNaN(before); k++)
                        if (x - dx * k >= 0 && y - dy * k >= 0) before = floor[(y - dy * k) * width + x - dx * k];
                    for (int k = 1; k <= 2 && float.IsNaN(after); k++)
                        if (x + dx * k < width && y + dy * k < height) after = floor[(y + dy * k) * width + x + dx * k];
                    if (!float.IsNaN(before) && !float.IsNaN(after) && MathF.Abs(before - after) <= Step) { seams[y * width + x] = MathF.Min(before, after); break; }
                }
            }
        floor = seams;
        // What movement collides with between a step and head height over a cell's floor blocks it.
        var blocked = new bool[width * height];
        Span<Vector3> clipped = stackalloc Vector3[9], cell = stackalloc Vector3[9];
        foreach (var (a, b, c, physical) in surfaces)
        {
            if (!physical) continue;
            int n = Clip([a, b, c], level - 6, level + ObstacleHigh + 6, clipped);
            if (n < 2) continue;
            float lx = float.MaxValue, ly = float.MaxValue, hx = float.MinValue, hy = float.MinValue;
            for (int k = 0; k < n; k++) { lx = MathF.Min(lx, clipped[k].X); ly = MathF.Min(ly, clipped[k].Y); hx = MathF.Max(hx, clipped[k].X); hy = MathF.Max(hy, clipped[k].Y); }
            int x0 = Math.Max(0, (int)MathF.Floor((lx - min.X) / Cell)), x1 = Math.Min(width - 1, (int)MathF.Floor((hx - min.X) / Cell));
            int y0 = Math.Max(0, (int)MathF.Floor((ly - min.Y) / Cell)), y1 = Math.Min(height - 1, (int)MathF.Floor((hy - min.Y) / Cell));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int i = y * width + x;
                    if (blocked[i] || float.IsNaN(floor[i])) continue;
                    int m = Clip(clipped[..n], floor[i] + ObstacleLow, floor[i] + ObstacleHigh, cell);
                    if (m >= 2 && Overlaps(cell[..m], min.X + x * Cell, min.Y + y * Cell, min.X + (x + 1) * Cell, min.Y + (y + 1) * Cell)) blocked[i] = true;
                }
        }
        // Clearance: a two-pass chamfer distance (3-4) from every cell that cannot be stood on.
        var clearance = new float[width * height];
        const float Far = 1e9f;
        for (int i = 0; i < clearance.Length; i++) clearance[i] = float.IsNaN(floor[i]) || blocked[i] ? 0 : Far;
        // A ledge (a drop or rise of more than a step to a neighbour) is an edge too: the way keeps off it.
        for (int y = 1; y < height - 1; y++)
            for (int x = 1; x < width - 1; x++)
            {
                int i = y * width + x;
                if (clearance[i] == 0) continue;
                foreach (int n in (ReadOnlySpan<int>)[i - 1, i + 1, i - width, i + width])
                    if (!float.IsNaN(floor[n]) && MathF.Abs(floor[n] - floor[i]) > Step) { clearance[i] = 0; break; }
            }
        // The grid's own border is an edge.
        for (int x = 0; x < width; x++) clearance[x] = clearance[(height - 1) * width + x] = 0;
        for (int y = 0; y < height; y++) clearance[y * width] = clearance[y * width + width - 1] = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x; float d = clearance[i];
                if (d == 0) continue;
                if (x > 0) d = MathF.Min(d, clearance[i - 1] + 1);
                if (y > 0) d = MathF.Min(d, clearance[i - width] + 1);
                if (x > 0 && y > 0) d = MathF.Min(d, clearance[i - width - 1] + 1.4142f);
                if (x < width - 1 && y > 0) d = MathF.Min(d, clearance[i - width + 1] + 1.4142f);
                clearance[i] = d;
            }
        for (int y = height - 1; y >= 0; y--)
            for (int x = width - 1; x >= 0; x--)
            {
                int i = y * width + x; float d = clearance[i];
                if (d == 0) continue;
                if (x < width - 1) d = MathF.Min(d, clearance[i + 1] + 1);
                if (y < height - 1) d = MathF.Min(d, clearance[i + width] + 1);
                if (x < width - 1 && y < height - 1) d = MathF.Min(d, clearance[i + width + 1] + 1.4142f);
                if (x > 0 && y < height - 1) d = MathF.Min(d, clearance[i + width - 1] + 1.4142f);
                clearance[i] = d;
            }
        for (int i = 0; i < clearance.Length; i++) clearance[i] *= Cell;
        return new(min, width, height, floor, blocked, clearance, level);
    }

    private static bool IsFlat(Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Cross(b - a, c - a); float length = normal.Length();
        return length > 1e-6f && MathF.Abs(normal.Z) / length > .7f;
    }

    /// <summary>The part of a convex polygon between two heights (at most nine corners).</summary>
    private static int Clip(ReadOnlySpan<Vector3> polygon, float low, float high, Span<Vector3> result)
    {
        Span<Vector3> work = stackalloc Vector3[9];
        int count = ClipPlane(polygon, low, true, work);
        return count == 0 ? 0 : ClipPlane(work[..count], high, false, result);
        static int ClipPlane(ReadOnlySpan<Vector3> input, float z, bool keepAbove, Span<Vector3> output)
        {
            int count = 0;
            for (int i = 0; i < input.Length; i++)
            {
                var p = input[i]; var q = input[(i + 1) % input.Length];
                bool pIn = keepAbove ? p.Z >= z : p.Z <= z, qIn = keepAbove ? q.Z >= z : q.Z <= z;
                if (pIn && count < output.Length) output[count++] = p;
                if (pIn != qIn && count < output.Length) output[count++] = Vector3.Lerp(p, q, (z - p.Z) / (q.Z - p.Z));
            }
            return count;
        }
    }

    /// <summary>Whether a convex polygon seen from above overlaps a square (separating axes).</summary>
    private static bool Overlaps(ReadOnlySpan<Vector3> polygon, float x0, float y0, float x1, float y1)
    {
        float lx = float.MaxValue, ly = float.MaxValue, hx = float.MinValue, hy = float.MinValue;
        foreach (var p in polygon) { lx = MathF.Min(lx, p.X); ly = MathF.Min(ly, p.Y); hx = MathF.Max(hx, p.X); hy = MathF.Max(hy, p.Y); }
        if (hx < x0 || lx > x1 || hy < y0 || ly > y1) return false;
        for (int i = 0; i < polygon.Length; i++)
        {
            var p = polygon[i]; var q = polygon[(i + 1) % polygon.Length];
            var axis = new Vector2(q.Y - p.Y, p.X - q.X);
            if (axis.LengthSquared() < 1e-10f) continue;
            float pl = float.MaxValue, ph = float.MinValue;
            foreach (var v in polygon) { float d = axis.X * v.X + axis.Y * v.Y; pl = MathF.Min(pl, d); ph = MathF.Max(ph, d); }
            float sl = float.MaxValue, sh = float.MinValue;
            foreach (var (cx, cy) in (ReadOnlySpan<(float, float)>)[(x0, y0), (x1, y0), (x1, y1), (x0, y1)])
            { float d = axis.X * cx + axis.Y * cy; sl = MathF.Min(sl, d); sh = MathF.Max(sh, d); }
            if (ph < sl || sh < pl) return false;
        }
        return true;
    }

    private static readonly (int X, int Y, float Length)[] Neighbours =
        [(1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1), (1, 1, 1.4142f), (1, -1, 1.4142f), (-1, 1, 1.4142f), (-1, -1, 1.4142f)];

    /// <summary>
    /// A path of cell centres from <paramref name="from"/> to within <paramref name="arrive"/> of <paramref name="goal"/>,
    /// through the middle of halls and around the given people (each kept <paramref name="personRadius"/> clear).
    /// When there is none: null, or with <paramref name="closest"/> the way to the reachable cell nearest the goal.
    /// </summary>
    public Vector2[]? FindPath(Vector2 from, Vector2 goal, float arrive, ReadOnlySpan<Vector2> people, float personRadius = 3, bool closest = false)
    {
        if (!TryCell(from, out int sx, out int sy)) return null;
        int count = Width * Height, start = sy * Width + sx;
        var cost = new float[count];
        Array.Fill(cost, float.PositiveInfinity);
        var previous = new int[count];
        // People: no way through within their radius, a little extra cost just beyond it.
        var crowd = new float[count];
        foreach (var person in people)
        {
            if (!TryCell(person, out int px, out int py)) continue;
            int reach = (int)MathF.Ceiling(personRadius * 2 / Cell);
            for (int y = Math.Max(0, py - reach); y <= Math.Min(Height - 1, py + reach); y++)
                for (int x = Math.Max(0, px - reach); x <= Math.Min(Width - 1, px + reach); x++)
                {
                    float d = Vector2.Distance(CellCenter(x, y), person);
                    float extra = d < personRadius ? float.PositiveInfinity : d < personRadius * 2 ? 2 * (personRadius * 2 - d) / personRadius : 0;
                    crowd[y * Width + x] = MathF.Max(crowd[y * Width + x], extra);
                }
        }
        float Heuristic(int i) => MathF.Max(0, Vector2.Distance(CellCenter(i % Width, i / Width), goal) - arrive);
        var open = new PriorityQueue<int, float>();
        cost[start] = 0; previous[start] = -1; open.Enqueue(start, Heuristic(start));
        int found = -1, nearest = start;
        float nearestDistance = float.MaxValue;
        while (open.TryDequeue(out int i, out float priority))
        {
            if (priority > cost[i] + Heuristic(i) + 1e-3f) continue;
            float distance = Vector2.Distance(CellCenter(i % Width, i / Width), goal);
            if (distance <= arrive) { found = i; break; }
            if (distance < nearestDistance) { nearestDistance = distance; nearest = i; }
            int x = i % Width, y = i / Width;
            foreach (var (dx, dy, length) in Neighbours)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= Width || ny >= Height) continue;
                int n = ny * Width + nx;
                // A cell too close to a wall is only left, never entered (the avatar may start there).
                if (_clearance[n] < Radius || float.IsInfinity(crowd[n])) continue;
                if (MathF.Abs(_floor[n] - _floor[i]) > Step && i != start) continue;
                // Diagonal moves do not cut a corner.
                if (dx != 0 && dy != 0 && (_clearance[y * Width + nx] < Radius || _clearance[ny * Width + x] < Radius)) continue;
                float tight = MathF.Max(0, Middle - _clearance[n]) / Middle;
                float next = cost[i] + length * Cell * (1 + 6 * tight * tight + crowd[n]);
                if (next >= cost[n]) continue;
                cost[n] = next; previous[n] = i;
                open.Enqueue(n, next + Heuristic(n));
            }
        }
        if (found < 0 && (!closest || nearest == start)) return null;
        if (found < 0) found = nearest;
        var path = new List<Vector2>();
        for (int i = found; i >= 0; i = previous[i]) path.Add(CellCenter(i % Width, i / Width));
        path.Reverse();
        return [.. path];
    }
}
