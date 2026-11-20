using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Room-box tree from retail 8D9360/8D9CB0 (build) and 8DA110 (point
/// query). Construction draws from the caller's CRT random source exactly as
/// retail does (two draws per plane try), so later effects see the same state.</summary>
public sealed class EnbRetailInfluenceSpatialTree
{
    private const int MaximumPlaneTries = 300;
    private const float ClassifyEpsilon = 1e-6f; // 0B0C9C8
    private const float ChildPadding = 1e-4f;    // 0AE7840

    private readonly Vector3[] boxCenter, boxExtent;
    private readonly List<Node> nodes = [];
    private readonly EnbRetailCrtRandomSource random;
    public int RandomDraws { get; private set; }

    private sealed class Node
    {
        public Vector3 Center, Extent;
        public int First = -1, Second = -1;
        public List<int> Records = []; // head first
    }

    private struct Plane
    {
        public float Cost;
        public int CountA, CountB;
        public Vector3 MinA, MaxA, MinB, MaxB;
        public int Axis;
        public float Coordinate;
        public static Plane Empty => new()
        {
            Cost = float.MaxValue,
            MinA = new(float.MaxValue), MaxA = new(-float.MaxValue),
            MinB = new(float.MaxValue), MaxB = new(-float.MaxValue),
        };
    }

    public EnbRetailInfluenceSpatialTree(IReadOnlyList<(Vector3 Center, Vector3 Extent)> boxes,
        EnbRetailCrtRandomSource random)
    {
        this.random = random;
        boxCenter = new Vector3[boxes.Count];
        boxExtent = new Vector3[boxes.Count];
        for (int i = 0; i < boxes.Count; i++) (boxCenter[i], boxExtent[i]) = boxes[i];
        // Insertion prepends each record to one leaf; 8D9CB0 then pops that
        // leaf's head into a fresh root, which restores insertion order.
        var root = new Node();
        nodes.Add(root);
        // The moves (and so the bounds unions) run from the last box down.
        for (int i = boxes.Count - 1; i >= 0; i--)
        {
            root.Records.Insert(0, i);
            if (i == boxes.Count - 1) { root.Center = boxCenter[i]; root.Extent = boxExtent[i]; }
            else Union(root, i);
        }
        Split(0);
    }

    public int[] Query(Vector3 point)
    {
        var result = new List<int>();
        Query(0, point, result);
        result.Reverse(); // 8C8F20 prepends each hit
        return result.ToArray();
    }

    private void Query(int index, Vector3 p, List<int> result)
    {
        while (index >= 0)
        {
            Node node = nodes[index];
            if (!Within(p, node.Center, node.Extent)) return;
            foreach (int record in node.Records)
                if (Within(p, boxCenter[record], boxExtent[record])) result.Add(record);
            if (node.Second >= 0) Query(node.Second, p, result);
            index = node.First;
        }
    }

    // Each axis fails only when |p-c| > e; NaN comparisons pass.
    private static bool Within(Vector3 p, Vector3 c, Vector3 e)
        => !(MathF.Abs(p.X - c.X) > e.X) && !(MathF.Abs(p.Y - c.Y) > e.Y) && !(MathF.Abs(p.Z - c.Z) > e.Z);

    // 8D9CB0 union: bounds are re-derived from the stored center/extent each time.
    private void Union(Node node, int record)
    {
        Vector3 c = node.Center, e = node.Extent, rc = boxCenter[record], re = boxExtent[record];
        float loX = c.X - e.X, loY = c.Y - e.Y, loZ = c.Z - e.Z;
        float hiX = c.X + e.X, hiY = e.Y + c.Y, hiZ = e.Z + c.Z;
        float r;
        r = rc.X - re.X; if (!(r >= loX)) loX = r;
        r = rc.Y - re.Y; if (!(r >= loY)) loY = r;
        r = rc.Z - re.Z; if (!(r >= loZ)) loZ = r;
        r = re.X + rc.X; if (r > hiX) hiX = r;
        r = re.Y + rc.Y; if (r > hiY) hiY = r;
        r = re.Z + rc.Z; if (r > hiZ) hiZ = r;
        node.Center = new((hiX + loX) * 0.5f, (hiY + loY) * 0.5f, (hiZ + loZ) * 0.5f);
        node.Extent = new((hiX - loX) * 0.5f, (hiY - loY) * 0.5f, (hiZ - loZ) * 0.5f);
    }

    private void Split(int index)
    {
        Node node = nodes[index];
        if (node.Records.Count <= 2) return;
        int[] boxes = node.Records.ToArray();
        Plane best = ChoosePlane(boxes);
        if (best.Cost == float.MaxValue || float.IsNaN(best.Cost)) return;
        var first = new Node();
        var second = new Node();
        // 8DBFF0 pops the head and prepends it to the chosen child.
        foreach (int record in boxes)
            (Classify(best, record) == 1 ? first : second).Records.Insert(0, record);
        node.Records.Clear();
        first.Center = Mid(best.MinA, best.MaxA); first.Extent = Half(best.MinA, best.MaxA);
        second.Center = Mid(best.MinB, best.MaxB); second.Extent = Half(best.MinB, best.MaxB);
        if (first.Records.Count > 0)
        {
            nodes.Add(first);
            node.First = nodes.Count - 1;
            Split(node.First);
        }
        if (second.Records.Count > 0)
        {
            nodes.Add(second);
            node.Second = nodes.Count - 1;
            Split(node.Second);
        }
    }

    private static Vector3 Mid(Vector3 min, Vector3 max)
        => new((min.X + max.X) * 0.5f, (max.Y + min.Y) * 0.5f, (max.Z + min.Z) * 0.5f);
    private static Vector3 Half(Vector3 min, Vector3 max)
        => new((max.X - min.X) * 0.5f + ChildPadding, (max.Y - min.Y) * 0.5f + ChildPadding, (max.Z - min.Z) * 0.5f + ChildPadding);

    private Plane ChoosePlane(int[] boxes)
    {
        Plane best = Plane.Empty;
        int tries = Math.Min(boxes.Length, MaximumPlaneTries);
        for (int t = 0; t < tries; t++)
        {
            Plane plane = Plane.Empty;
            int box = boxes[(int)Draw() % boxes.Length];
            Vector3 c = boxCenter[box], e = boxExtent[box];
            switch ((int)Draw() % 6)
            {
                case 0: plane.Axis = 0; plane.Coordinate = e.X + c.X; break;
                case 1: plane.Axis = 0; plane.Coordinate = c.X - e.X; break;
                case 2: plane.Axis = 1; plane.Coordinate = e.Y + c.Y; break;
                case 3: plane.Axis = 1; plane.Coordinate = c.Y - e.Y; break;
                case 4: plane.Axis = 2; plane.Coordinate = e.Z + c.Z; break;
                default: plane.Axis = 2; plane.Coordinate = c.Z - e.Z; break;
            }
            Cost(ref plane, boxes);
            if (!(plane.Cost >= best.Cost)) best = plane;
        }
        if (BitConverter.SingleToUInt32Bits(best.Cost) == 0x7f7fffffu) ExhaustivePlane(ref best, boxes);
        return best;
    }

    // 8DCB60: retail's face table here differs from 8DC990's (cases 1..5
    // read other fields or zero); kept as executed.
    private void ExhaustivePlane(ref Plane best, int[] boxes)
    {
        foreach (int box in boxes)
        {
            Vector3 c = boxCenter[box], e = boxExtent[box];
            for (int face = 0; face < 6; face++)
            {
                Plane plane = Plane.Empty;
                switch (face)
                {
                    case 0: plane.Axis = 0; plane.Coordinate = e.X + c.X; break;
                    case 1: plane.Axis = 0; plane.Coordinate = c.X - c.Y; break;
                    case 2: plane.Axis = 1; plane.Coordinate = c.Y + c.Y; break;
                    case 3: plane.Axis = 1; plane.Coordinate = 0; break;
                    case 4: plane.Axis = 2; plane.Coordinate = c.Z + c.Z; break;
                    default: plane.Axis = 2; plane.Coordinate = 0; break;
                }
                Cost(ref plane, boxes);
                if (!(plane.Cost >= best.Cost)) best = plane;
            }
        }
    }

    // 8E2FA0: 1 above the plane; below or on the plane is side B.
    private bool Above(in Plane plane, int box)
        => boxCenter[box][plane.Axis] - plane.Coordinate > ClassifyEpsilon;
    private int Classify(in Plane plane, int box) => Above(plane, box) ? 1 : 2;

    // 8DCD30
    private void Cost(ref Plane plane, int[] boxes)
    {
        foreach (int box in boxes)
        {
            bool a = Above(plane, box);
            if (a) plane.CountA++; else plane.CountB++;
            Vector3 c = boxCenter[box], e = boxExtent[box];
            if (Zero(e.X) && Zero(e.Y) && Zero(e.Z)) continue;
            Vector3 lo = new(c.X - e.X, c.Y - e.Y, c.Z - e.Z);
            Vector3 hi = new(e.X + c.X, e.Y + c.Y, e.Z + c.Z);
            if (a) Grow(ref plane.MinA, ref plane.MaxA, lo, hi);
            else Grow(ref plane.MinB, ref plane.MaxB, lo, hi);
        }
        Vector3 da = plane.MaxA - plane.MinA, db = plane.MaxB - plane.MinB;
        float costA = (float)plane.CountA * da.Z * da.Y * da.X;
        float costB = (float)plane.CountB * db.Z * db.Y * db.X;
        plane.Cost = plane.CountA == 0 || plane.CountB == 0 ? float.MaxValue : costA + costB;
    }

    private static bool Zero(float value) => value == 0 || float.IsNaN(value);

    private static void Grow(ref Vector3 min, ref Vector3 max, Vector3 lo, Vector3 hi)
    {
        if (!(lo.X >= min.X)) min.X = lo.X;
        if (!(lo.Y >= min.Y)) min.Y = lo.Y;
        if (!(lo.Z >= min.Z)) min.Z = lo.Z;
        if (hi.X > max.X) max.X = hi.X;
        if (hi.Y > max.Y) max.Y = hi.Y;
        if (hi.Z > max.Z) max.Z = hi.Z;
    }

    private uint Draw()
    {
        RandomDraws++;
        return random.Next();
    }
}
