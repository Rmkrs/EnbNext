using System.Numerics;
using Register=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>MeshModel triangle-plane consumer0092C090. The device-created
/// retail thread uses x87 PC24; authored source positions and triangle order
/// are retained. Owner dirty flags and normal generation are separate.</summary>
public static class EnbCurrentRetailMeshGeometryRuntime
{
    /// <summary>0092C590/008D3860: replace bounds using supplied vertices,
    /// center from the box midpoint and the retail-inflated half diagonal.</summary>
    public static void CalculateBounds(ReadOnlySpan<Vector3> positions,ref Vector3 minimum,ref Vector3 maximum,
        ref Vector3 center,ref float radius,ref uint flags)
    {
        minimum=maximum=center=Vector3.Zero;radius=0;
        if(positions.IsEmpty)return;
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        minimum=maximum=positions[0];
        foreach(var value in positions[1..])
        {
            if(!(value.X>minimum.X))minimum.X=value.X;
            if(!(value.Y>minimum.Y))minimum.Y=value.Y;
            if(!(value.Z>minimum.Z))minimum.Z=value.Z;
            if(value.X>=maximum.X)maximum.X=value.X;
            if(value.Y>=maximum.Y)maximum.Y=value.Y;
            if(value.Z>=maximum.Z)maximum.Z=value.Z;
        }
        //Y/Z sums are stored before the multiplication; X stays in ST0.
        center=new((float)(((Register)minimum.X+maximum.X)*.5),
            (float)((Register)(float)((Register)minimum.Y+maximum.Y)*.5),
            (float)((Register)(float)((Register)minimum.Z+maximum.Z)*.5));
        Register x=(Register)maximum.X-center.X,y=(Register)maximum.Y-center.Y,z=(Register)maximum.Z-center.Z;
        Register halfDiagonal=Register.Sqrt(z*z+y*y+x*x);
        radius=(float)(halfDiagonal*BitConverter.UInt32BitsToSingle(0x3f800054));
        flags&=~1u;
    }

    /// <summary>0092D460 after the caller has made vertex/normal storage unique.
    /// Retained bounds use signed axis scaling and radius uses the ordered
    /// maximum scale. An absent tree stays absent; an existing one is copied
    /// and scaled or rebuilt from the resulting positions.</summary>
    public static void Scale(Span<Vector3> positions,ReadOnlySpan<ushort> triangleIndices,Vector3 scale,
        ref Vector3 minimum,ref Vector3 maximum,ref Vector3 center,ref float radius,
        ref uint flags,ref EnbCurrentRetailMeshAabTree? tree,ref uint crtThreadSeed)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        Vector3 Multiply(Vector3 value)=>new((float)((Register)scale.X*value.X),(float)((Register)scale.Y*value.Y),(float)((Register)scale.Z*value.Z));
        for(int vertex=0;vertex<positions.Length;vertex++)positions[vertex]=Multiply(positions[vertex]);
        minimum=Multiply(minimum);maximum=Multiply(maximum);center=Multiply(center);
        float largest=scale.X>scale.Y?scale.X:scale.Y;
        if(!(largest>scale.Z))largest=scale.Z;
        radius=(float)((Register)largest*radius);
        static bool EqualOrUnordered(float a,float b)=>a==b||float.IsNaN(a)||float.IsNaN(b);
        if(!EqualOrUnordered(scale.X,scale.Y)||!EqualOrUnordered(scale.Y,scale.Z))flags|=4;
        flags|=2;
        if(tree is null)return;
        double tolerance=BitConverter.Int64BitsToDouble(0x3f1a36e2e0000000);
        double firstDifference=Math.Abs((double)((Register)scale.X-scale.Y));
        double secondDifference=Math.Abs((double)((Register)scale.X-scale.Z));
        tree=!(firstDifference>=tolerance)&&!(secondDifference>=tolerance)
            ?tree.CopyScaled(scale.X):EnbCurrentRetailMeshAabTree.Build(positions,triangleIndices,ref crtThreadSeed);
    }

    /// <summary>0092C680's lazy buffer getter through MeshModel virtual+10.
    /// Empty meshes retain both dirty flags and incoming normal storage.</summary>
    public static void GetNormals(ReadOnlySpan<Vector3> positions,ReadOnlySpan<ushort> triangleIndices,
        ReadOnlySpan<uint> vertexJoinMap,Span<Vector3> normals,Span<Vector4> trianglePlanes,ref uint flags)
    {
        if((flags&4)==0)return;
        if(triangleIndices.Length==0 || positions.Length==0)return;
        if(triangleIndices.Length%3!=0 || normals.Length<positions.Length || trianglePlanes.Length<triangleIndices.Length/3 ||
            (!vertexJoinMap.IsEmpty && vertexJoinMap.Length<positions.Length))throw new ArgumentException("Mesh buffers do not define bounded model storage.");
        foreach(ushort index in triangleIndices)if(index>=positions.Length)throw new ArgumentException("Triangle index exceeds model storage.");
        if(!vertexJoinMap.IsEmpty)
            foreach(uint index in vertexJoinMap[..positions.Length])if(index>=positions.Length)throw new ArgumentException("Vertex join index exceeds model storage.");
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        //0092C220 calls virtual+0C directly, regenerating every triangle plane.
        for(int triangle=0;triangle<triangleIndices.Length/3;triangle++)
        {
            int start=triangle*3;
            trianglePlanes[triangle]=CalculateTrianglePlane(positions[triangleIndices[start]],positions[triangleIndices[start+1]],positions[triangleIndices[start+2]]);
        }
        flags&=~2u;
        normals[..positions.Length].Clear(); //008D37C0
        for(int triangle=0;triangle<triangleIndices.Length/3;triangle++)
        {
            var plane=trianglePlanes[triangle];
            for(int corner=0;corner<3;corner++)
            {
                int vertex=triangleIndices[triangle*3+corner];
                if(!vertexJoinMap.IsEmpty)vertex=(int)vertexJoinMap[vertex];
                var normal=normals[vertex];
                normals[vertex]=new((float)((Register)normal.X+plane.X),(float)((Register)normal.Y+plane.Y),(float)((Register)normal.Z+plane.Z));
            }
        }
        if(!vertexJoinMap.IsEmpty)
            for(int vertex=0;vertex<positions.Length;vertex++)
                normals[vertex]=vertexJoinMap[vertex]==vertex?Normalize(normals[vertex]):normals[(int)vertexJoinMap[vertex]];
        //008D37F0 runs for both branches, including already-normalized joins.
        for(int vertex=0;vertex<positions.Length;vertex++)normals[vertex]=Normalize(normals[vertex]);
        flags&=~4u;
    }

    private static Vector3 Normalize(Vector3 value)
    {
        Register square=(Register)value.X*value.X+(Register)value.Y*value.Y+(Register)value.Z*value.Z;
        if(square==0)return value;
        Register inverse=EnbRetailLineCoalescing.ReciprocalSquareRoot((float)square);
        return new((float)(inverse*value.X),(float)(inverse*value.Y),(float)(inverse*value.Z));
    }

    public static Vector4 CalculateTrianglePlane(Vector3 first,Vector3 second,Vector3 third)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        Register x=(Register)second.X-first.X,y=(Register)second.Y-first.Y,z=(Register)second.Z-first.Z;
        float otherX=(float)((Register)third.X-first.X),otherY=(float)((Register)third.Y-first.Y);
        Register otherZ=(Register)third.Z-first.Z;
        float normalX=(float)(otherZ*y-(Register)otherY*z);
        float normalY=(float)(z*otherX-otherZ*x);
        Register normalZ=(Register)otherY*x-y*otherX;
        float squareX=(float)((Register)normalX*normalX);
        Register square=(Register)normalY*normalY+normalZ*normalZ+squareX;
        if(square!=0)
        {
            Register inverse=EnbRetailLineCoalescing.ReciprocalSquareRoot((float)square);
            normalX=(float)(inverse*normalX);normalY=(float)(inverse*normalY);
            normalZ=inverse*(float)normalZ;
        }
        Register distance=-((Register)normalY*first.Y+(Register)normalX*first.X+normalZ*first.Z);
        return new(normalX,normalY,(float)normalZ,(float)distance);
    }
}
