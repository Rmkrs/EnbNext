namespace EnbNext.Rendering;

/// <summary>Index words authored by retail 00478240, consumed by 00474B30/004745D0.</summary>
public sealed record EnbRetailTerrainIndices(ushort[] Strip, ushort[] Seams)
{
    public static EnbRetailTerrainIndices[] Build(int side)
    {
        var levels = new EnbRetailTerrainIndices[5];
        for (int level = 4; level >= 0; level--)
        {
            int step = 1 << level;
            var strip = new List<ushort>();
            bool forward = true;
            for (int y = step; y < side; y += step)
            {
                if (forward)
                {
                    for (int x = 0; x < side - step; x += step)
                    {
                        strip.Add(unchecked((ushort)(y * side + x)));
                        strip.Add(unchecked((ushort)((y - step) * side + x)));
                    }
                    ushort last = unchecked((ushort)((y + 1) * side - 1));
                    strip.Add(last);
                    strip.Add(unchecked((ushort)((y - step + 1) * side - 1)));
                    strip.Add(last);
                    strip.Add(last);
                }
                else
                {
                    strip.Add(unchecked((ushort)((y - step + 1) * side - 1)));
                    strip.Add(unchecked((ushort)((y + 1) * side - 1)));
                    ushort last = 0;
                    for (int x = side - 1; x > 0; x -= step)
                    {
                        strip.Add(unchecked((ushort)((y - step) * side - step + x)));
                        last = unchecked((ushort)(y * side - step + x));
                        strip.Add(last);
                    }
                    strip.Add(last);
                    strip.Add(last);
                }
                forward = !forward;
            }
            var seams = new List<ushort>();
            if (level != 0)
            {
                int half = step >> 1;
                for (int y = step; y <= side; y += step)
                {
                    seams.Add(unchecked((ushort)((y - step) * side)));
                    seams.Add(unchecked((ushort)(y * side)));
                    seams.Add(unchecked((ushort)((y - half) * side)));
                }
                for (int y = step; y < side; y += step)
                {
                    seams.Add(unchecked((ushort)((y - step + 1) * side - 1)));
                    seams.Add(unchecked((ushort)((y - half + 1) * side - 1)));
                    seams.Add(unchecked((ushort)((y + 1) * side - 1)));
                }
                for (int x = step; x < side; x += step)
                {
                    seams.Add(unchecked((ushort)(x - step)));
                    seams.Add(unchecked((ushort)(x - half)));
                    seams.Add(unchecked((ushort)x));
                }
                int bottom = (side - 1) * side;
                for (int x = step; x < side; x += step)
                {
                    seams.Add(unchecked((ushort)(bottom - step + x)));
                    seams.Add(unchecked((ushort)(bottom + x)));
                    seams.Add(unchecked((ushort)(bottom - half + x)));
                }
            }
            levels[level] = new(strip.ToArray(), seams.ToArray());
        }
        return levels;
    }

    /// <summary>Expand the strip and the edge ranges selected by patch+14 into GPU triangle-list input.</summary>
    public uint[] Triangles(EnbTerrainLodNeighborMask neighbors)
    {
        var result = new List<uint>();
        for (int i = 2; i < Strip.Length; i++)
        {
            ushort a = Strip[i - 2], b = Strip[i - 1], c = Strip[i];
            if (a == b || b == c || a == c) continue;
            if ((i & 1) != 0) (a, b) = (b, a);
            result.Add(a); result.Add(b); result.Add(c);
        }
        int edgeLength = Seams.Length / 4;
        for (int edge = 0; edge < 4; edge++)
            if (((byte)neighbors & (1 << edge)) != 0)
                for (int i = edge * edgeLength; i < (edge + 1) * edgeLength; i++)
                    result.Add(Seams[i]);
        return result.ToArray();
    }
}
