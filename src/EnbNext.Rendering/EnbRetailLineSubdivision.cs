using System.Numerics;
using NativeReal = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

// Current retail 009C2390. Input positions are already in renderer/camera space.
// Shared centerline construction for procedural and particle GPU strips.
public static class EnbRetailLineSubdivision
{
    public static uint ReserveNonFrozenDraws(int count)
    {
        return EnbRetailParticleRandomStreams.Process.ReserveLineWords(count);
    }
    public readonly record struct Node(Vector3 Position,float Fraction)
    {
        public float V {get;init;}
        public Vector4 Color {get;init;}
    }
    public sealed record Batch(int FirstSourcePoint,Node[] Nodes);
    internal sealed class OwnedBatch(int firstSourcePoint,Node[] nodes)
    {
        public int FirstSourcePoint {get;internal set;}=firstSourcePoint;
        public Node[] Nodes {get;internal set;}=nodes;
        internal readonly NodeRange Active=new();
    }
    internal sealed class NodeRange:IReadOnlyList<Node>
    {
        internal Node[] Values=[];
        public int Count {get;internal set;}
        public Node this[int i] => (uint)i<(uint)Count?Values[i]:throw new ArgumentOutOfRangeException(nameof(i));
        public IEnumerator<Node> GetEnumerator(){for(int i=0;i<Count;i++)yield return Values[i];}
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    internal sealed class Storage
    {
        internal readonly List<OwnedBatch> Batches=[];
        internal int Count;
    }
    public sealed record Polyline(IReadOnlyList<Batch> Batches,uint NextCounter);

    // 009BEF8E..B2 bounds each call to 128 output segments; 009C21FF
    // overlaps one original point. Positions must already be in renderer space.
    public static Polyline BuildBatches(IReadOnlyList<Node> points,int depth,float amplitude,bool freeze,uint seed=0,uint counter=0,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Double53)
    {
        using var arithmetic = new EnbRetailLineArithmetic.Scope(precision);
        if(depth is <0 or >7 || points.Count <2)
            throw new InvalidDataException("Invalid line batch size/depth");
        int sourceSegments=128>>depth;
        var batches=new List<Batch>();
        for(int first=0;first<points.Count-1;first+=sourceSegments)
        {
            var input=points.Skip(first).Take(sourceSegments+1).ToArray();
            batches.Add(new(first,Subdivide(input,depth,amplitude,freeze,seed,ref counter)));
        }
        return new(batches.AsReadOnly(),counter);
    }

    internal static void BuildOwned(IReadOnlyList<Node> points,int depth,float amplitude,bool freeze,uint counter,
        EnbRetailLinePrecision precision,Storage storage)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
        if(depth is <0 or >7 || points.Count<2)throw new InvalidDataException("Invalid line batch size/depth");
        int segments=128>>depth;storage.Count=0;
        for(int first=0;first<points.Count-1;first+=segments)
        {
            int count=Math.Min(segments+1,points.Count-first);
            if(storage.Count==storage.Batches.Count)storage.Batches.Add(new(first,new Node[129]));
            var batch=storage.Batches[storage.Count++];batch.FirstSourcePoint=first;
            Subdivide(points,depth,amplitude,freeze,0,ref counter,batch.Nodes,first,count);
            batch.Active.Values=batch.Nodes;batch.Active.Count=((count-1)<<depth)+1;
        }
    }

    private static Node[] Subdivide(IReadOnlyList<Node> input,int depth,float amplitude,bool freeze,uint seed,ref uint nextCounter,Node[]? destination=null,int first=0,int count=-1)
    {
        static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
        static bool AttributesFinite(Node n)=>float.IsFinite(n.V)&&float.IsFinite(n.Color.X)&&float.IsFinite(n.Color.Y)&&float.IsFinite(n.Color.Z)&&float.IsFinite(n.Color.W);
        if(count<0)count=input.Count;
        bool valid=true;for(int i=0;i<count;i++){var n=input[first+i];if(!Finite(n.Position)||!AttributesFinite(n)||!float.IsFinite(n.Fraction)){valid=false;break;}}
        if(!valid||depth is <0 or >7||!float.IsFinite(amplitude))
            throw new InvalidDataException("Invalid line subdivision parameters");
        var nodes=destination??new Node[((count-1)<<depth)+1];
        uint counter=nextCounter;
        // 009C2390 constructs a fresh local (0,0) generator for frozen noise.
        if(freeze){seed=0;counter=0;}
        int index=0;
        float Next()=> (float)((NativeReal)EnbRetailSignedRandom.Word(seed,counter++)*(1.0/2147483648.0));
        void Split(Node a,Node b,int remaining,float noise)
        {
            if(remaining==0){nodes[index++]=a;return;}
            float first=Next(),second=Next(),third=Next();
            // 009C2758..009C27B9 deliberately swaps the first two draws in
            // the frozen branch. 00A15AE0's nonfrozen box sampler uses XYZ.
            var offset=freeze?new Vector3(second,first,third):new Vector3(first,second,third);
            // 009C289D..28B0 averages V without wrapping. 009C28CC..295A
            // averages all four color channels, without packing or clamping.
            // R remains extended until multiplication; G/B/A sums spill first.
            //009C27F4/2801 spill X/Y noise products; Z stays extended.
            //009C2816/2820 spill Y/Z sums; X stays extended through halving.
            var position=new Vector3(
                (float)(((NativeReal)a.Position.X+b.Position.X)*.5+(float)((NativeReal)offset.X*noise)),
                (float)((NativeReal)(float)((NativeReal)(float)((NativeReal)a.Position.Y+b.Position.Y)*.5)+(float)((NativeReal)offset.Y*noise)),
                (float)((NativeReal)(float)((NativeReal)(float)((NativeReal)a.Position.Z+b.Position.Z)*.5)+(NativeReal)offset.Z*noise));
            var mid=new Node(position,(a.Fraction+b.Fraction)*.5f)
            {
                V=(float)(((NativeReal)a.V+b.V)*.5),
                Color=new((float)(((NativeReal)a.Color.X+b.Color.X)*.5),(float)((NativeReal)(float)((NativeReal)a.Color.Y+b.Color.Y)*.5),
                    (float)((NativeReal)(float)((NativeReal)a.Color.Z+b.Color.Z)*.5),(float)((NativeReal)(float)((NativeReal)a.Color.W+b.Color.W)*.5))
            };
            Split(a,mid,remaining-1,noise*.5f);
            Split(mid,b,remaining-1,noise*.5f);
        }
        for(int segment=0;segment<count-1;segment++)
            Split(input[first+segment],input[first+segment+1],depth,amplitude);
        nodes[index]=input[first+count-1];
        if(!freeze)nextCounter=counter;
        return nodes;
    }
}
