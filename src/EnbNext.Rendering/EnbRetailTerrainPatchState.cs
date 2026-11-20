using System.Numerics;

namespace EnbNext.Rendering;

public readonly record struct EnbRetailTerrainPatch(Vector3 Translation, int Level,
    EnbTerrainLodNeighborMask Neighbors, bool Visible);


/// <summary>Terrain patch detail levels, as the retail client updates them each frame. When the
/// camera's patch cell changes, every patch is placed (relative to the camera when the terrain wraps),
/// its level is the number of distance-threshold doublings passed (threshold 8..128 cells by land
/// detail, levels 0..4), a breadth-first pass from the camera's patch caps neighbours at one level
/// coarser, and neighbour masks mark finer sides. Visibility tests each patch box against the camera's
/// six planes every frame.</summary>
public sealed class EnbRetailTerrainPatchState
{
    private static readonly int[] Thresholds = [8, 16, 24, 32, 48, 64, 96, 128];
    private readonly int grid, half, mask, cells;
    private readonly float xScale, yScale, heightScale, maximumHeight;
    private readonly bool wrapping;
    private readonly float[] midpoints;
    private readonly int[] levels, neighbors;
    private readonly bool[] visited;
    private readonly Vector3[] translations;
    private readonly EnbRetailTerrainPatch[] patches;
    private readonly Queue<(int X, int Y)> queue = new();
    private bool dirty = true;
    private int lastX, lastY, lastZ;

    public EnbRetailTerrainPatchState(int grid, int cells, ushort maximumHeight,
        float xScale, float yScale, float heightScale, bool wrapping, ReadOnlySpan<float> midpoints)
    {
        if (grid <= 0 || !BitOperations.IsPow2(grid) || midpoints.Length != checked(grid * grid))
            throw new ArgumentException("Terrain patch grid must be square with a power-of-two width.");
        this.grid = grid; half = grid >> 1; mask = grid - 1; this.cells = cells;
        this.xScale = xScale; this.yScale = yScale; this.heightScale = heightScale; this.maximumHeight = maximumHeight;
        this.wrapping = wrapping; this.midpoints = midpoints.ToArray();
        int count = grid * grid;
        levels = new int[count]; neighbors = new int[count]; visited = new bool[count];
        translations = new Vector3[count]; patches = new EnbRetailTerrainPatch[count];
    }

    public IReadOnlyList<EnbRetailTerrainPatch> Update(ReadOnlySpan<float> cameraMatrix,
        Vector4 projectionBounds, EnbRenderClipRange clip, bool orthographic, int landLod)
    {
        var camera = new Vector3(cameraMatrix[3], cameraMatrix[7], cameraMatrix[11]);
        float patchWorld = cells * xScale, halfPatch = patchWorld * 0.5f;
        float inversePatch = 1f / patchWorld, inverseCell = 1f / xScale;
        int cx = (int)(inversePatch * camera.X), cy = (int)(inversePatch * camera.Y), cz = (int)(inversePatch * camera.Z);
        if (dirty || cx != lastX || cy != lastY || cz != lastZ)
        {
            dirty = false;
            for (int x = 0; x < grid; x++)
            for (int y = 0; y < grid; y++)
            {
                int index = y * grid + x;
                float px, py;
                if (wrapping) { px = (((x - cx) & mask) - half + cx) * patchWorld; py = (((y - cy) & mask) - half + cy) * patchWorld; }
                else { px = (x - half) * patchWorld; py = (y - half) * patchWorld; }
                translations[index] = new(px, py, 0);
                float dx = camera.X - (px + halfPatch), dy = camera.Y - (py + halfPatch), dz = camera.Z - heightScale * midpoints[index];
                float distance = MathF.Sqrt(dz * dz + dy * dy + dx * dx);
                int cellDistance = (int)(distance * inverseCell), threshold = Thresholds[landLod], level = 0;
                while (level < 4 && cellDistance >= threshold) { threshold <<= 1; level++; }
                levels[index] = level; neighbors[index] = 0; visited[index] = false;
            }
            Refine((half + cx) & mask, (half + cy) & mask);
            for (int x = 0; x < grid; x++)
            for (int y = 0; y < grid; y++)
            {
                int index = y * grid + x, level = levels[index];
                if (level == 0) continue;
                if (levels[y * grid + ((x - 1) & mask)] < level) neighbors[index] |= 1;
                if (levels[y * grid + ((x + 1) & mask)] < level) neighbors[index] |= 2;
                if (levels[((y - 1) & mask) * grid + x] < level) neighbors[index] |= 4;
                if (levels[((y + 1) & mask) * grid + x] < level) neighbors[index] |= 8;
            }
            lastX = cx; lastY = cy; lastZ = cz;
        }
        var view = new CameraPlanes(cameraMatrix, projectionBounds, clip, orthographic);
        float sizeX = xScale * cells, sizeY = yScale * cells, top = maximumHeight * heightScale;
        for (int i = 0; i < patches.Length; i++)
        {
            var low = translations[i]; var high = new Vector3(sizeX + low.X, sizeY + low.Y, top + low.Z);
            bool visible = !view.Outside((low + high) * 0.5f, (high - low) * 0.5f);
            patches[i] = new(low, levels[i], (EnbTerrainLodNeighborMask)neighbors[i], visible);
        }
        return patches;
    }

    public IReadOnlyList<EnbRetailTerrainPatch> Update(EnbResolvedRenderCamera camera, int landLod)
    {
        var m = EnbBridgeCameraComposition.RetailTransform(camera);
        ReadOnlySpan<float> matrix = [m.M11, m.M21, m.M31, m.M41, m.M12, m.M22, m.M32, m.M42, m.M13, m.M23, m.M33, m.M43];
        return Update(matrix, camera.Projection.StoredProjectionBounds
            ?? throw new InvalidOperationException("Terrain requires the resolved retail projection bounds."),
            camera.Projection.ClipRange, camera.Projection.IsOrthographic, landLod);
    }

    // Breadth-first from the camera's patch: each visited patch caps its unvisited neighbours (left,
    // right, up, down, within the grid) at its own level + 1 (at most 4).
    private void Refine(int x, int y)
    {
        queue.Clear(); queue.Enqueue((x, y));
        while (queue.TryDequeue(out var cell))
        {
            int index = cell.Y * grid + cell.X;
            if (visited[index]) continue;
            visited[index] = true;
            int allowed = Math.Min(levels[index] + 1, 4);
            if (cell.X - 1 >= 0) Cap(cell.X - 1, cell.Y, allowed);
            if (cell.X + 1 < grid) Cap(cell.X + 1, cell.Y, allowed);
            if (cell.Y - 1 >= 0) Cap(cell.X, cell.Y - 1, allowed);
            if (cell.Y + 1 < grid) Cap(cell.X, cell.Y + 1, allowed);
        }
    }

    private void Cap(int x, int y, int allowed)
    {
        int index = y * grid + x;
        if (visited[index]) return;
        queue.Enqueue((x, y));
        if (levels[index] > allowed) levels[index] = allowed;
    }

    /// <summary>Camera planes in world space; a box is outside when it lies fully beyond one plane.</summary>
    private readonly struct CameraPlanes
    {
        private readonly Vector4[] planes;
        public CameraPlanes(ReadOnlySpan<float> m, Vector4 bounds, EnbRenderClipRange clip, bool orthographic)
        {
            var origin = new Vector3(m[3], m[7], m[11]);
            var right = new Vector3(m[0], m[4], m[8]); var up = new Vector3(m[1], m[5], m[9]); var back = new Vector3(m[2], m[6], m[10]);
            // Camera space: x = right, y = up, depth = -back. Each plane as (world normal, offset): outside when dot(n, p) + w > 0.
            Vector4 Plane(float a, float b, float c, float k)
            {
                var n = a * right + b * up - c * back;
                return new(n, k - Vector3.Dot(n, origin));
            }
            float l = bounds.X, bt = bounds.Y, r = bounds.Z, t = bounds.W;
            planes = orthographic
                ? [Plane(0, 0, -1, clip.NearPlane), Plane(0, 0, 1, -clip.FarPlane)]
                : [Plane(0, 0, -1, clip.NearPlane), Plane(0, 0, 1, -clip.FarPlane), Plane(-1, 0, l, 0), Plane(1, 0, -r, 0), Plane(0, -1, bt, 0), Plane(0, 1, -t, 0)];
        }
        public bool Outside(Vector3 center, Vector3 extent)
        {
            foreach (var p in planes)
            {
                var n = new Vector3(p.X, p.Y, p.Z);
                float distance = Vector3.Dot(n, center) + p.W, reach = MathF.Abs(n.X) * extent.X + MathF.Abs(n.Y) * extent.Y + MathF.Abs(n.Z) * extent.Z;
                // Retail's planes sit slightly outside the exact ones; keep anything within 0.1% of the
                // box size, so a patch retail would draw is never hidden.
                float tolerance = 1e-3f * n.Length() * MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)) + 1e-4f * MathF.Abs(distance);
                if (distance - reach > tolerance) return true;
            }
            return false;
        }
    }
}
