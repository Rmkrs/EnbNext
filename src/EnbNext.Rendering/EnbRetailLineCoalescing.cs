using System.Numerics;
using NativeReal = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

// Raw009C0BC2..009C149A. Records retain the native neutral flag offsets;
// their construction and the zipper consumer are separate from this mutation.
public static class EnbRetailLineCoalescing
{
    public record struct Intersection(uint Count, int Segment, Vector3 Ray,
        Vector3 Center, float V, Vector4 Color, bool Flag34, bool Flag35);
    public readonly record struct SegmentPlanes(Vector3 Main, Vector3 Upper, Vector3 Lower);
    public readonly record struct Result(Intersection[] Upper, Intersection[] Lower)
    {
        public int UpperCount {get;init;}
        public int LowerCount {get;init;}
        public Vector3 RetainedCandidateRay { get; init; }
        public bool HasRetainedCandidateRay { get; init; }
    }
    public readonly record struct Scratch(Intersection[] Upper, Intersection[] Lower,
        SegmentPlanes[] Planes, Vector3 RetainedCandidateRay) {public int Count {get;init;}};
    public readonly record struct Vertex(Vector3 Position, uint Argb, Vector2 Uv);
    public readonly record struct Geometry(Vertex[] Vertices, uint[] Indices)
    {public int VertexCount {get;init;} public int IndexCount {get;init;}}
    public sealed class Storage
    {
        internal readonly Intersection[] Upper=new Intersection[130],Lower=new Intersection[130],CompactUpper=new Intersection[130],CompactLower=new Intersection[130];
        internal readonly SegmentPlanes[] Planes=new SegmentPlanes[130];
        internal readonly Vector3[] Directions=new Vector3[130];
        internal readonly List<Vertex> Vertices=new(258);
        internal readonly List<uint> Indices=new(768);
        internal readonly Vertex[] VertexOutput=new Vertex[258];
        internal readonly uint[] IndexOutput=new uint[768];
    }

    //009BEFDA..009BF1EC. Numerics row notation for native savedView*model.
    // The first output coordinate accumulates Z/Y/X; the other two use X/Y/Z.
    public static Matrix4x4 ComposeModelView(Matrix4x4 model,Matrix4x4 view,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Double53)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
        Vector3 Row(float x,float y,float z,bool translation=false)=>new(
            (float)((NativeReal)z*view.M31+(NativeReal)y*view.M21+(NativeReal)x*view.M11+(translation?view.M41:0)),
            (float)((NativeReal)x*view.M12+(NativeReal)y*view.M22+(NativeReal)z*view.M32+(translation?view.M42:0)),
            (float)((NativeReal)x*view.M13+(NativeReal)y*view.M23+(NativeReal)z*view.M33+(translation?view.M43:0)));
        var a=Row(model.M11,model.M12,model.M13);var b=Row(model.M21,model.M22,model.M23);
        var c=Row(model.M31,model.M32,model.M33);var d=Row(model.M41,model.M42,model.M43,true);
        return new(a.X,a.Y,a.Z,0,b.X,b.Y,b.Z,0,c.X,c.Y,c.Z,0,d.X,d.Y,d.Z,1);
    }

    //008D3285..32D6, native 3x4 column rows adapted to Numerics row notation.
    public static Vector3 TransformPoint(Vector3 point, Matrix4x4 matrix,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Double53)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        return new(
            (float)((NativeReal)point.Z * matrix.M31 + (NativeReal)point.Y * matrix.M21 + (NativeReal)point.X * matrix.M11 + matrix.M41),
            (float)((NativeReal)point.Z * matrix.M32 + (NativeReal)point.X * matrix.M12 + (NativeReal)point.Y * matrix.M22 + matrix.M42),
            (float)((NativeReal)point.Z * matrix.M33 + (NativeReal)point.X * matrix.M13 + (NativeReal)point.Y * matrix.M23 + matrix.M43));
    }

    //009695A0 ->00969640 ->009696A0 leaves bounds half-Y/half-Z and
    //00969652 return-address bits at the later renderer's EBP-18/-14/-10.
    // Bounds use original object-space points and maximum authored subdivision,
    // independently of the selected rendering level and generated noise draws.
    public static Vector3 ProceduralIncomingRay(IReadOnlyList<Vector3> points,
        float width, int maximumSubdivision, float noise, uint boundsReturnAddress = 0x00969652,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Double53)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        float half = (float)(width * .5);
        float maxY = (float)((NativeReal)points.Max(p => p.Y) + half);
        float minY = (float)((NativeReal)points.Min(p => p.Y) - half);
        float maxZ = (float)((NativeReal)points.Max(p => p.Z) + half);
        NativeReal minZ = (NativeReal)points.Min(p => p.Z) - half;
        if (maximumSubdivision != 0)
        {
            float midMinY = float.PositiveInfinity, midMaxY = float.NegativeInfinity;
            float midMinZ = float.PositiveInfinity, midMaxZ = float.NegativeInfinity;
            for (int i = 0; i < points.Count - 1; i++)
            {
                float y = (float)((float)((NativeReal)points[i].Y + points[i + 1].Y) * .5);
                float z = (float)((float)((NativeReal)points[i].Z + points[i + 1].Z) * .5);
                midMinY = Math.Min(midMinY, y); midMaxY = Math.Max(midMaxY, y);
                midMinZ = Math.Min(midMinZ, z); midMaxZ = Math.Max(midMaxZ, z);
            }
            NativeReal expansion = (NativeReal)noise * 2.0 + half;
            minY = Math.Min(minY, (float)(midMinY - expansion));
            maxY = Math.Max(maxY, (float)(midMaxY + expansion));
            maxZ = Math.Max(maxZ, (float)(midMaxZ + expansion));
            NativeReal expandedMinZ = midMinZ - expansion;
            if (expandedMinZ < minZ) minZ = (float)expandedMinZ;
        }
        return new((float)((float)((NativeReal)maxY - minY) * .5),
            (float)((maxZ - minZ) * .5), BitConverter.UInt32BitsToSingle(boundsReturnAddress));
    }

    //009C149A..009C1E6E consumes independent compacted edge counts. Counts
    // advance the original subdivided point cursor; Center is not that cursor.
    public static Geometry Emit(IReadOnlyList<EnbRetailLineSubdivision.Node> nodes,
        Result intersections, float upperU, float lowerU, Vector2 scroll,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Double53,Storage? storage=null)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        var upper = intersections.Upper; var lower = intersections.Lower;
        var vertices=storage?.Vertices??new List<Vertex>();var indices=storage?.Indices??new List<uint>();
        vertices.Clear();indices.Clear();
        int upperCount=intersections.UpperCount==0?upper.Length:intersections.UpperCount;
        int lowerCount=intersections.LowerCount==0?lower.Length:intersections.LowerCount;
        void Add(Intersection record, int point, float u, int projectionMode)
        {
            var p = nodes[point].Position; var ray = record.Ray;
            NativeReal dot = (NativeReal)p.Y * ray.Y + (NativeReal)p.Z * ray.Z + (NativeReal)p.X * ray.X;
            if (point == 0 && projectionMode == 2)
                dot = (NativeReal)p.Z * ray.Z + (NativeReal)p.X * ray.X + (NativeReal)p.Y * ray.Y;
            NativeReal xy = projectionMode == 2 ? dot : (float)dot;
            NativeReal z = projectionMode == 0 ? (float)dot : dot;
            // DC CC/CB/CA target ST4/ST3/ST2: each RGB channel is scaled,
            // followed by DE C9 for alpha. FISTP uses truncation, no clamp.
            var c = record.Color;
            // The conversion block switches RC to truncation before FMUL.
            // For these clamped/weighted channels, truncating the exact product
            // gives the same integer as PC24's significand truncation then FISTP.
            uint packed = unchecked((uint)(int)(c.Z * 255.0) | (uint)(int)(c.W * 255.0) << 24 |
                (uint)(int)(c.X * 255.0) << 16 | (uint)(int)(c.Y * 255.0) << 8);
            vertices.Add(new(new((float)(ray.X * xy), (float)(ray.Y * xy), (float)(ray.Z * z)),
                packed, new((float)((NativeReal)u + scroll.X), (float)((NativeReal)record.V + scroll.Y))));
        }
        Add(upper[1], 0, upperU, 0);
        Add(lower[1], 0, lowerU, 2);
        int up = 1, down = 1, point = 0;
        uint upRemaining = upper[up].Count, downRemaining = lower[down].Count;
        uint lastUp = 0, lastDown = 1;
        void SkipShared()
        {
            uint skip = Math.Min(upRemaining, downRemaining) - 1;
            upRemaining -= skip; downRemaining -= skip; point += checked((int)skip);
        }
        SkipShared();
        do
        {
            uint next = (uint)vertices.Count;
            indices.Add(lastUp);indices.Add(lastDown);indices.Add(next);
            point++;
            if (upRemaining == 1 && downRemaining == 1)
            {
                indices.Add(lastDown);indices.Add(next+1);indices.Add(next);
                up++; down++;
                upRemaining = upper[up].Count; downRemaining = lower[down].Count;
                Add(upper[up], point, upperU, 1);
                Add(lower[down], point, lowerU, 2);
                lastUp = next; lastDown = next + 1;
            }
            else if (upRemaining > 1)
            {
                upRemaining--; down++;
                downRemaining = lower[down].Count;
                Add(lower[down], point, lowerU, 1);
                lastDown = next;
            }
            else
            {
                downRemaining--; up++;
                upRemaining = upper[up].Count;
                Add(upper[up], point, upperU, 1);
                lastUp = next;
            }
            SkipShared();
        } while ((up < upperCount - 1 || upRemaining != 1) &&
                 (down < lowerCount - 1 || downRemaining != 1));
        if(storage is null)return new(vertices.ToArray(),indices.ToArray()){VertexCount=vertices.Count,IndexCount=indices.Count};
        vertices.CopyTo(storage.VertexOutput);indices.CopyTo(storage.IndexOutput);
        return new(storage.VertexOutput,storage.IndexOutput){VertexCount=vertices.Count,IndexCount=indices.Count};
    }

    //009BF3A1..009C0BC2, after view transformation/subdivision. Native record0
    // is a sentinel; real point0 resides at record1. The endpoint cut planes
    // occupy0/N and replace Main at1, while internal cut planes replace each
    // following segment's Main after its direction has served construction.
    public static Scratch Construct(IReadOnlyList<EnbRetailLineSubdivision.Node> nodes, float halfWidth, Vector3 retainedCandidateRay,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Double53,Storage? storage=null)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        int count = nodes.Count;
        if (count is < 2 or > 129) throw new InvalidDataException("Invalid native line batch");
        var planes=storage?.Planes??new SegmentPlanes[count+1];
        var directions=storage?.Directions??new Vector3[count+1];
        var upper=storage?.Upper??new Intersection[count+1];
        var lower=storage?.Lower??new Intersection[count+1];
        Array.Clear(planes,0,count+1);Array.Clear(directions,0,count+1);Array.Clear(upper,0,count+1);Array.Clear(lower,0,count+1);
        bool flipped = false;
        for (int i = 1; i < count; i++)
        {
            var p = nodes[i - 1].Position;
            var direction = directions[i] = NormalizeDirection(nodes[i].Position - p);
            NativeReal projection = -((NativeReal)direction.Y * p.Y + (NativeReal)direction.X * p.X + (NativeReal)direction.Z * p.Z);
            //009BF4AB..4CC: Y/Z products spill; X remains in x87 through
            // the side-plane cross product.
            NativeReal x = p.X + projection * direction.X;
            float y = (float)(p.Y + (float)(projection * direction.Y));
            float z = (float)(p.Z + (float)(projection * direction.Z));
            var side = Normalize(new((float)((NativeReal)direction.Y * z - (NativeReal)direction.Z * y),
                (float)(direction.Z * x - direction.X * (NativeReal)z),
                (float)(direction.X * (NativeReal)y - direction.Y * x)));
            NativeReal plusX = p.X + side.X * (NativeReal)halfWidth;
            float plusY = (float)(p.Y + side.Y * (NativeReal)halfWidth);
            float plusZ = (float)(p.Z + side.Z * (NativeReal)halfWidth);
            var minus = new Vector3((float)(p.X + (float)(side.X * (NativeReal)(-halfWidth))),
                (float)(p.Y + (float)(side.Y * (NativeReal)(-halfWidth))), (float)(p.Z + side.Z * (NativeReal)(-halfWidth)));
            //009BF5B2 retains displaced X until009BF632. Y/Z have spilled.
            var up = Normalize(new((float)((NativeReal)plusY * direction.Z - (NativeReal)plusZ * direction.Y),
                (float)((NativeReal)plusZ * direction.X - plusX * direction.Z),
                (float)(plusX * direction.Y - (NativeReal)plusY * direction.X)));
            var down = Normalize(Cross(direction, minus));
            if (i > 1)
            {
                var before = Normalize(Cross(nodes[i - 1].Position, nodes[i - 2].Position));
                var after = Normalize(Cross(nodes[i].Position, nodes[i - 1].Position));
                bool flag = Dot(before, after) < 0;
                upper[i] = upper[i] with { Flag34 = flag };
                lower[i] = lower[i] with { Flag34 = flag };
                if (flag) flipped = !flipped;
            }
            if (flipped) (up, down) = (-down, -up);
            planes[i] = new(direction, up, down);
        }
        upper[0] = lower[0] = new(0, 0, Vector3.UnitX, Vector3.Zero, 0, Vector4.Zero, true, false);
        Intersection Record(int index, Vector3 ray, bool flag34, bool flag35)
        {
            var node = nodes[index - 1];
            return new(1, index, ray, node.Position, node.V, node.Color, flag34, flag35);
        }
        var firstUp = NormalizeFull(Project(nodes[0].Position, planes[1].Upper));
        var firstDown = NormalizeFull(Project(nodes[0].Position, planes[1].Lower));
        upper[1] = Record(1, firstUp, true, false);
        lower[1] = Record(1, firstDown, true, false);
        Vector3 Cut(Vector3 up, Vector3 down, Vector3 direction, bool spillScale)
        {
            var cut = Cross(up, down);
            // The later cuts retain cross Y across its FST and use that
            // extended value in009C0B04; Z is reloaded from its float spill.
            NativeReal crossY = spillScale ? (NativeReal)up.Z * down.X - (NativeReal)up.X * down.Z : cut.Y;
            NativeReal square = (NativeReal)cut.Z * cut.Z + crossY * crossY + (float)((NativeReal)cut.X * cut.X);
            if (square != 0)
            {
                NativeReal scale = ReciprocalSquareRoot((float)square);
                //009C03ED /009C0B18 spill the scale before multiplying Y/Z;
                //009BFEE8 retains it for all three initial cut components.
                NativeReal yzScale = spillScale ? (float)scale : scale;
                cut = new((float)(cut.X * scale), (float)(cut.Y * yzScale), (float)(cut.Z * yzScale));
            }
            return Dot(cut, direction) > 0 ? cut : -cut;
        }
        var firstCut = Cut(firstUp, firstDown, directions[1], false);
        planes[0] = new(firstCut, firstCut, firstCut);
        planes[1] = planes[1] with { Main = firstCut };
        var lastUp = NormalizeFull(Project(nodes[^1].Position, planes[count - 1].Upper, true));
        var lastDown = NormalizeFull(Project(nodes[^1].Position, planes[count - 1].Lower));
        upper[count] = Record(count, lastUp, true, false);
        lower[count] = Record(count, lastDown, true, false);
        var lastCut = Cut(lastUp, lastDown, directions[count - 1], true);
        planes[count] = new(lastCut, lastCut, lastCut);
        for (int i = 2; i < count; i++)
        {
            (Vector3 Ray, bool Flag) Ray(Vector3 previous, Vector3 next, bool lowerEdge)
            {
                NativeReal dot = Dot(previous, next);
                if (Math.Abs((double)dot) >= (NativeReal).9f)
                {
                    return (ParallelConstructionRay(nodes[i - 1].Position, previous, next, dot > 0, lowerEdge), true);
                }
                var ray = NormalizeIntersectionCross(Cross(previous, next));
                if (Dot(ray, nodes[i - 1].Position) < 0) ray = -ray;
                return (ray, false);
            }
            var up = Ray(planes[i - 1].Upper, planes[i].Upper, false);
            var down = Ray(planes[i - 1].Lower, planes[i].Lower, true);
            upper[i] = Record(i, up.Ray, upper[i].Flag34, up.Flag);
            lower[i] = Record(i, down.Ray, lower[i].Flag34, down.Flag);
            //009C0AA4..0B92 leaves the current cut ray in EBP-24/-20/-1C;
            // EBP-18/-14/-10 is separate retained coalescing scratch.
            planes[i] = planes[i] with { Main = Cut(up.Ray, down.Ray, directions[i], true) };
        }
        return new(upper,lower,planes,retainedCandidateRay){Count=count+1};
    }

    // Each input includes the native sentinel at0 and Count real records at1..
    // Count. Both edges occupy independent slots of the interleaved scratch.
    public static Result Compact(IReadOnlyList<Intersection> upper,
        IReadOnlyList<Intersection> lower, IReadOnlyList<SegmentPlanes> planes,
        float halfWidth, float abortFactor, Vector3 retainedCandidateRay,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Double53,
        bool hasRetainedCandidateRay = true,Storage? storage=null,int activeCount=0)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        if (upper.Count < 2 || lower.Count != upper.Count)
            throw new InvalidDataException("Invalid line intersection scratch");
        int activeLength=activeCount==0?upper.Count:activeCount;
        var upperRecords=storage?.CompactUpper??new Intersection[activeLength];var lowerRecords=storage?.CompactLower??new Intersection[activeLength];
        for(int i=0;i<activeLength;i++){upperRecords[i]=upper[i];lowerRecords[i]=lower[i];}
        Span<int> counts=stackalloc int[]{activeLength-1,activeLength-1};
        bool changed;
        do
        {
            changed = false;
            for (int edge = 0; edge < 2; edge++)
            {
                var records=edge==0?upperRecords:lowerRecords;
                int originalCount = counts[edge], input = 1, output = 1;
                Vector3 Edge(int segment) => edge == 0 ? planes[segment].Upper : planes[segment].Lower;
                while (input < originalCount)
                {
                    int aIndex = input, bIndex = input + 1;
                    var a = records[aIndex];
                    //009C0C4F caches the initial A cut for the entire attempt
                    // chain;009C137C updates only B after a successful merge.
                    var aMain = planes[a.Segment].Main;
                    var aEdge = Edge(a.Segment);
                    while (true)
                    {
                        var b = records[bIndex];
                        if (!EnbRetailLineJoin.IsMergeEligibleRaw(a.Ray, a.Flag34, b.Ray, b.Flag34,
                            planes[b.Segment].Main, Edge(b.Segment), aMain,
                            Edge(records[output - 1].Segment))) break;

                        uint count = checked(a.Count + b.Count);
                        //009C0D5A..0DBF: both operands use A.Count / total;
                        // do not silently substitute a conventional lerp.
                        NativeReal weight = ((NativeReal)1.0 / count) * a.Count;
                        float spilledWeight = (float)weight;
                        static float Combine(float av, float bv, float aw, NativeReal bw)
                            => (float)((float)(av * (NativeReal)aw) + bv * bw);
                        var center = new Vector3((float)(a.Center.X * (NativeReal)spilledWeight + b.Center.X * weight),
                            Combine(a.Center.Y, b.Center.Y, spilledWeight, weight),
                            Combine(a.Center.Z, b.Center.Z, spilledWeight, weight));
                        float v = (float)(a.V * (NativeReal)spilledWeight + b.V * weight);
                        var color = new Vector4((float)(a.Color.X * (NativeReal)spilledWeight + b.Color.X * weight),
                            (float)((float)(a.Color.Y * (NativeReal)spilledWeight) + (float)(b.Color.Y * weight)),
                            (float)((float)(a.Color.Z * (NativeReal)spilledWeight) + (float)(b.Color.Z * weight)),
                            (float)((float)(a.Color.W * (NativeReal)spilledWeight) + (float)(b.Color.W * weight)));

                        var previous = Edge(records[output - 1].Segment);
                        var next = Edge(b.Segment);
                        NativeReal dot = Dot(previous, next);
                        Vector3 ray;
                        bool flag35;
                        if (Math.Abs((double)dot) < (NativeReal).9f)
                        {
                            ray = Normalize(Cross(previous, next));
                            if (Dot(ray, center) < 0) ray = -ray;
                            flag35 = false;
                        }
                        else
                        {
                            // Correct the retail uninitialized-stack read at
                            //009C1037: a projection needs an actual preceding
                            //candidate. Until one exists, leave these records
                            //unmerged. No substitute ray is manufactured.
                            if (a.Flag35 && !hasRetainedCandidateRay) break;
                            //009C1037 reads the retained scratch ray at EBP-18,
                            // not A.Ray. Rejected attempts also retain it.
                            ray = ParallelCoalescingRay(aEdge, previous, next, dot > 0,
                                retainedCandidateRay, a.Flag35);
                            flag35 = true;
                        }
                        retainedCandidateRay = ray;
                        hasRetainedCandidateRay = true;
                        if (!EnbRetailLineJoin.WithinMergeAbortRaw(ray, a.Center, b.Center, halfWidth, abortFactor)) break;
                        a = new(count, b.Segment, ray, center, v, color, a.Flag34 || b.Flag34, flag35);
                        records[aIndex] = a;
                        changed = true;
                        counts[edge]--;
                        input++;
                        bIndex++;
                        if (input == originalCount) break;
                    }
                    //009C1383..13DB copies all fields except+35. The existing
                    // destination byte persists, including between passes.
                    records[output] = a with { Flag35 = records[output].Flag35 };
                    output++;
                    input++;
                }
                if (input == originalCount)
                    records[output] = records[input] with { Flag35 = records[output].Flag35 };
            }
        } while (changed);
        return new(storage is null?upperRecords.AsSpan(0,counts[0]+1).ToArray():upperRecords,
            storage is null?lowerRecords.AsSpan(0,counts[1]+1).ToArray():lowerRecords)
            {UpperCount=counts[0]+1,LowerCount=counts[1]+1,RetainedCandidateRay=retainedCandidateRay,HasRetainedCandidateRay=hasRetainedCandidateRay};
    }

    internal static NativeReal Dot(Vector3 a, Vector3 b)
        => (NativeReal)a.Z * b.Z + (NativeReal)a.Y * b.Y + (NativeReal)a.X * b.X;
    internal static Vector3 Cross(Vector3 a, Vector3 b) => new(
        (float)((NativeReal)a.Y * b.Z - (NativeReal)a.Z * b.Y),
        (float)((NativeReal)a.Z * b.X - (NativeReal)a.X * b.Z),
        (float)((NativeReal)a.X * b.Y - (NativeReal)a.Y * b.X));
    internal static Vector3 Project(Vector3 point, Vector3 normal, bool xyz = false)
    {
        NativeReal dot = xyz ? (NativeReal)point.X * normal.X + (NativeReal)point.Y * normal.Y + (NativeReal)point.Z * normal.Z : Dot(point, normal);
        return new((float)((NativeReal)point.X - dot * normal.X),
            (float)((NativeReal)point.Y - (float)(dot * normal.Y)),
            (float)(point.Z - (float)(dot * normal.Z)));
    }
    internal static Vector3 Normalize(Vector3 value)
    {
        NativeReal square = (NativeReal)value.Z * value.Z + (NativeReal)value.Y * value.Y + (float)((NativeReal)value.X * value.X);
        if (square == 0) return value;
        NativeReal scale = ReciprocalSquareRoot((float)square);
        return new((float)(value.X * scale), (float)(value.Y * scale), (float)(value.Z * scale));
    }

    private static Vector3 NormalizeFull(Vector3 value)
    {
        NativeReal square = (NativeReal)value.X * value.X + (NativeReal)value.Y * value.Y + (NativeReal)value.Z * value.Z;
        if (square == 0) return value;
        NativeReal scale = ReciprocalSquareRoot((float)square);
        return new((float)(value.X * scale), (float)(value.Y * scale), (float)(value.Z * scale));
    }

    private static Vector3 NormalizeDirection(Vector3 value)
    {
        //009BF44E..45C accumulates Z-square, then X-square, then Y-square.
        NativeReal square = (NativeReal)value.Z * value.Z + (NativeReal)value.X * value.X + (NativeReal)value.Y * value.Y;
        if (square == 0) return value;
        NativeReal scale = ReciprocalSquareRoot((float)square);
        return new((float)(value.X * scale), (float)(value.Y * scale), (float)(value.Z * scale));
    }

    private static Vector3 NormalizeIntersectionCross(Vector3 value)
    {
        //009C0607..061D: no X-square float spill.
        NativeReal square = (NativeReal)value.Z * value.Z + (NativeReal)value.Y * value.Y + (NativeReal)value.X * value.X;
        if (square == 0) return value;
        NativeReal scale = ReciprocalSquareRoot((float)square);
        return new((float)(value.X * scale), (float)(value.Y * scale), (float)(value.Z * scale));
    }

    private static Vector3 ParallelCoalescingRay(Vector3 cut, Vector3 previous, Vector3 next,
        bool add, Vector3 retained, bool project)
    {
        //009C0FA3..1026 retains normalized Z through the projection/cross,
        // with X/Y spilled. Near-parallel cuts make that spill consequential.
        float x = (float)(previous.X + (add ? (NativeReal)next.X : -next.X));
        float y = (float)(previous.Y + (add ? (NativeReal)next.Y : -next.Y));
        NativeReal z = previous.Z + (add ? (NativeReal)next.Z : -next.Z);
        NativeReal square = z * z + (NativeReal)y * y + (float)((NativeReal)x * x);
        if (square != 0)
        {
            NativeReal scale = ReciprocalSquareRoot((float)square);
            x = (float)(x * scale); y = (float)(y * scale); z = (float)z * scale;
        }
        if (!project)
            return Normalize(new((float)(cut.Y * z - (NativeReal)cut.Z * y),
                (float)((NativeReal)cut.Z * x - cut.X * z),
                (float)((NativeReal)cut.X * y - (NativeReal)cut.Y * x)));
        NativeReal dot = z * retained.Z + (NativeReal)x * retained.X + (NativeReal)y * retained.Y;
        var ray = new Vector3((float)((NativeReal)retained.X - (float)(dot * x)),
            (float)((NativeReal)retained.Y - (float)(dot * y)), (float)(retained.Z - dot * z));
        square = (NativeReal)ray.X * ray.X + (NativeReal)ray.Z * ray.Z + (NativeReal)ray.Y * ray.Y;
        if (square == 0) return ray;
        NativeReal rayScale = ReciprocalSquareRoot((float)square);
        return new((float)(ray.X * rayScale), (float)(ray.Y * rayScale), (float)(ray.Z * rayScale));
    }

    private static Vector3 ParallelConstructionRay(Vector3 point, Vector3 previous, Vector3 next, bool add, bool lowerEdge)
    {
        //009C06D6..081A /009C095F..0A9D retain normal Z in x87 after
        // normalization, then retain projected Z through the length calculation.
        float x = (float)(previous.X + (add ? (NativeReal)next.X : -next.X));
        float y = (float)(previous.Y + (add ? (NativeReal)next.Y : -next.Y));
        NativeReal z = previous.Z + (add ? (NativeReal)next.Z : -next.Z);
        NativeReal square = z * z + (NativeReal)y * y + (float)((NativeReal)x * x);
        if (square != 0)
        {
            NativeReal scale = ReciprocalSquareRoot((float)square);
            x = (float)(x * scale); y = (float)(y * scale); z = (float)z * scale;
        }
        NativeReal dot = z * point.Z + (NativeReal)y * point.Y + (NativeReal)x * point.X;
        if (lowerEdge) dot = (NativeReal)x * point.X + z * point.Z + (NativeReal)y * point.Y;
        x = (float)((NativeReal)point.X - (float)(dot * x));
        y = (float)((NativeReal)point.Y - (float)(dot * y));
        z = point.Z - dot * z;
        square = (NativeReal)y * y + z * z + (NativeReal)x * x;
        if (square == 0) return new(x, y, (float)z);
        NativeReal outputScale = ReciprocalSquareRoot((float)square);
        return new((float)(x * outputScale), (float)(y * outputScale), (float)((float)z * outputScale));
    }

    //00405F1A ->00447C40. Three Newton steps with the native seed and
    // bitwise half argument; no platform sqrt or tuned approximation.
    internal static NativeReal ReciprocalSquareRoot(float square)
    {
        uint bits = BitConverter.SingleToUInt32Bits(square);
        NativeReal half = BitConverter.UInt32BitsToSingle(unchecked(bits - 0x800000));
        NativeReal value = BitConverter.UInt32BitsToSingle(unchecked(0xBE6EB508u - bits) >> 1);
        //DC CB/CA multiply ST3/ST2 by ST0 rather than replacing ST0;
        // preserve that optimized Newton expression's operand order.
        NativeReal squared = value * value * half;
        NativeReal first = 1.5 - squared;
        NativeReal corrected = squared * first * first;
        NativeReal second = 1.5 - corrected;
        return value * first * second * (1.5 - corrected * second * second);
    }
}
