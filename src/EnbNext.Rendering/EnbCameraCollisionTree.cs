using System.Numerics;

namespace EnbNext.Rendering;

// Retail 92AEA0 -> 9B3CC0/9B4540 retains a model-owned polygon tree.
// The partition is reused here, but bounds are conservative for Next's existing
// world-space floating-point leaf predicate; this is not a native arithmetic port.
internal sealed class EnbCameraCollisionTree
{
    private readonly EnbCurrentRetailMeshAabTree tree;
    private readonly IReadOnlyList<Vector3> positions;
    private readonly IReadOnlyList<uint> indices;
    private readonly (Vector3 Min,Vector3 Max)[] bounds;
    private EnbCameraCollisionTree(EnbCurrentRetailMeshAabTree tree,IReadOnlyList<Vector3> positions,
        IReadOnlyList<uint> indices,(Vector3,Vector3)[] bounds)
    {this.tree=tree;this.positions=positions;this.indices=indices;this.bounds=bounds;}

    public static EnbCameraCollisionTree? Create(EnbCurrentRetailMeshAabTree tree,
        IReadOnlyList<Vector3> positions,IReadOnlyList<uint> indices)
    {
        if(tree.Nodes.Count==0)return null;
        var bounds=new (Vector3 Min,Vector3 Max)[tree.Nodes.Count];
        var state=new byte[tree.Nodes.Count];
        var covered=new bool[indices.Count/3];
        var pending=new Stack<(int Node,bool Exit)>();pending.Push((0,false));
        while(pending.TryPop(out var item))
        {
            int index=item.Node;
            if((uint)index>=(uint)state.Length)return null;
            var node=tree.Nodes[index];
            if(item.Exit)
            {
                var a=bounds[(int)node.First];var b=bounds[(int)node.Second];
                bounds[index]=(Vector3.Min(a.Min,b.Min),Vector3.Max(a.Max,b.Max));state[index]=2;continue;
            }
            if(state[index]==1)return null; // cyclic data cannot accelerate a linear query
            if(state[index]==2)continue;
            state[index]=1;
            if((node.First&0x80000000)==0)
            {
                if(node.First>=state.Length || node.Second>=state.Length)return null;
                pending.Push((index,true));pending.Push(((int)node.Second,false));pending.Push(((int)node.First,false));
                continue;
            }
            uint start=node.First&0x7fffffff;
            if((ulong)start+node.Second>(ulong)tree.PolygonIndices.Count)return null;
            var min=new Vector3(float.PositiveInfinity);var max=new Vector3(float.NegativeInfinity);
            for(uint i=0;i<node.Second;i++)
            {
                uint polygon=tree.PolygonIndices[(int)(start+i)];
                if(polygon>=covered.Length)return null;
                covered[(int)polygon]=true;
                for(int vertex=0;vertex<3;vertex++)
                {
                    uint slot=indices[(int)polygon*3+vertex];if(slot>=positions.Count)return null;
                    var p=positions[(int)slot];
                    if(!Finite(p))return null;
                    min=Vector3.Min(min,p);max=Vector3.Max(max,p);
                }
            }
            bounds[index]=(min,max);state[index]=2;
        }
        // An incomplete authored partition must not remove triangles which the
        // established linear consumer would have visited.
        if(covered.Any(value=>!value))return null;
        return new(tree,positions,indices,bounds);
    }

    public bool Intersects(Matrix4x4 world,Vector3 center,Vector3 extent)=>Visit(0,world,center,extent);
    private bool Visit(int index,Matrix4x4 world,Vector3 center,Vector3 extent)
    {
        var bound=bounds[index];
        if(Separated(bound.Min,bound.Max,world,center,extent))return false;
        var node=tree.Nodes[index];
        if((node.First&0x80000000)==0)
            return Visit((int)node.First,world,center,extent)||Visit((int)node.Second,world,center,extent);
        uint start=node.First&0x7fffffff;
        for(uint i=0;i<node.Second;i++)
        {
            int triangle=(int)tree.PolygonIndices[(int)(start+i)]*3;
            if(EnbShipCameraCollision.StaticOverlap(
                Vector3.Transform(positions[(int)indices[triangle]],world)-center,
                Vector3.Transform(positions[(int)indices[triangle+1]],world)-center,
                Vector3.Transform(positions[(int)indices[triangle+2]],world)-center,extent))return true;
        }
        return false;
    }

    private static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
    internal static bool Separated(Vector3 min,Vector3 max,Matrix4x4 world,Vector3 center,Vector3 extent)
    {
        if(!Finite(min)||!Finite(max)||!Finite(center)||!Finite(extent))return false;
        var low=new Vector3(float.PositiveInfinity);var high=new Vector3(float.NegativeInfinity);
        // Identical transform/subtract operations to the leaf. Affine floating
        // arithmetic is monotone in each input component, including reflections.
        for(int corner=0;corner<8;corner++)
        {
            var point=new Vector3((corner&1)==0?min.X:max.X,(corner&2)==0?min.Y:max.Y,(corner&4)==0?min.Z:max.Z);
            var transformed=Vector3.Transform(point,world)-center;
            if(!Finite(transformed))return false;
            low=Vector3.Min(low,transformed);high=Vector3.Max(high,transformed);
        }
        if(!Finite(high-low))return false;
        for(int axis=0;axis<3;axis++)
        {
            float a=low[axis],b=high[axis];
            // Leaf SAT uses a + min(0,b-a,c-a), not min(a,b,c).
            // Enclose that stored subtraction/addition as well. This widened
            // interval avoids an epsilon and never tightens the old predicate.
            if(a<0){if(b>0)continue;(a,b)=(-b,-a);}
            float projected=a+MathF.Min(0,a-b);
            if(float.IsFinite(projected)&&projected>extent[axis])return true;
        }
        return false;
    }
}
