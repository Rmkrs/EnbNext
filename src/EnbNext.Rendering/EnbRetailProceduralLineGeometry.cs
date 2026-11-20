using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>009BF28B/009C2390 view-space subdivision and
/// 009BF3A1..009C1E6E intersection construction, coalescing and zipper.</summary>
public static class EnbRetailProceduralLineGeometry
{
    public sealed record Geometry(Vector3[] Positions,Vector2[] Uv,uint[] Indices)
    {
        public Vector3[] ViewPositions {get;init;}=[];
    }

    public static Geometry Build(IReadOnlyList<Vector3> points,EnbResolvedRenderCamera camera,
        int depth,float noise,bool freeze,float halfWidth,int uvMode,float tile,Vector2 scroll,
        IReadOnlyList<Vector3>? boundsPoints=null,int? maximumSubdivision=null,uint boundsReturnAddress=0x00969652,
        uint? noiseCounter=null,Matrix4x4? modelToWorld=null)
    {
        if(points.Count is <2 or >32 || !float.IsFinite(halfWidth) || halfWidth<=0 || uvMode is <0 or >2)
            throw new InvalidDataException("Invalid procedural line geometry");
        EnbRenderCameraMatrixCompiler.TryCreateWorldToCanonicalView(camera,out var view);
        if(modelToWorld is {} model)view=EnbRetailLineCoalescing.ComposeModelView(model,view,EnbRetailLinePrecision.Single24);
        Vector3 ToView(Vector3 p)=>EnbRetailLineCoalescing.TransformPoint(p,view,EnbRetailLinePrecision.Single24);
        Vector3 ToWorld(Vector3 p)=>camera.Position+camera.Basis.Right*p.X+camera.Basis.Up*p.Y-camera.Basis.Forward*p.Z;
        var input=points.Select((p,i)=>new EnbRetailLineSubdivision.Node(ToView(p),i)
            {V=uvMode==0?0:i*tile}).ToArray();
        if(depth is <0 or >7)throw new InvalidDataException("Invalid procedural line subdivision");
        //00969618 supplies null RGBA.009BF368..36E incorrectly adds the
        //next source-point offset to it, faulting at009C24AE in batch two.
        //Correct that retail bug: color remains absent in every batch. The
        //procedural caller uses its uniform color; node colors are not read
        //by this output path. Preserve native overlapping batches, noise,
        //retained coalescing scratch and index offsets below.
        int draws=checked((points.Count-1)*((1<<depth)-1)*3);
        uint counter=freeze?0:noiseCounter??EnbRetailLineSubdivision.ReserveNonFrozenDraws(draws);
        var expanded=EnbRetailLineSubdivision.BuildBatches(input,depth,noise,freeze,counter:counter,
            precision:EnbRetailLinePrecision.Single24);
        var retainedRay=EnbRetailLineCoalescing.ProceduralIncomingRay(boundsPoints??points,halfWidth*2,
            maximumSubdivision??depth,noise,boundsReturnAddress,EnbRetailLinePrecision.Single24);
        var vertices=new List<Vector3>();var viewVertices=new List<Vector3>();var uv=new List<Vector2>();var indices=new List<uint>();
        foreach(var batch in expanded.Batches)
        {
            var nodes=batch.Nodes;
            var scratch=EnbRetailLineCoalescing.Construct(nodes,halfWidth,retainedRay,EnbRetailLinePrecision.Single24);
            var compacted=EnbRetailLineCoalescing.Compact(scratch.Upper,scratch.Lower,scratch.Planes,
                halfWidth,1.5f,scratch.RetainedCandidateRay,EnbRetailLinePrecision.Single24);
            retainedRay=compacted.RetainedCandidateRay;
            var geometry=EnbRetailLineCoalescing.Emit(nodes,compacted,0,uvMode==2?0:1,scroll,EnbRetailLinePrecision.Single24);
            uint first=(uint)vertices.Count;
            foreach(var vertex in geometry.Vertices)
            {
                vertices.Add(ToWorld(vertex.Position));
                viewVertices.Add(vertex.Position);
                uv.Add(vertex.Uv);
            }
            indices.AddRange(geometry.Indices.Select(i=>first+i));
        }
        return new(vertices.ToArray(),uv.ToArray(),indices.ToArray()){ViewPositions=viewVertices.ToArray()};
    }
}
