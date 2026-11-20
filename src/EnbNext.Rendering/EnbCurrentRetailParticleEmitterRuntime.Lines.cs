using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public static partial class EnbCurrentRetailParticleEmitterRuntime
{
    //009BE950 consumes words0..6 only; frame mode and reserved words do
    //not participate.009BEF8E batches128 segments, so depth8 cannot progress.
    public static bool SupportsJetLine(EnbCurrentRetailParticleEmitterDefinition d)
    {
        var w=d.LinePropertyWords;
        return d.RenderMode==2 && w.Length==16 && w[1]<=7 &&
            Enumerable.Range(2,5).All(i=>float.IsFinite(BitConverter.UInt32BitsToSingle(w[i]))) &&
            d.Size.Keys.Count>0 && float.IsFinite(d.Size.Keys[0].Value);
    }
    public static bool SupportsEngineTrail(EnbCurrentRetailParticleEmitterDefinition d) => SupportsJetLine(d);
    // One physical buffer/invocation owns all changing line products. These
    // lists are never shared with another queued invocation. The numeric scratch
    // is bounded by retail's128-segment batch, not by the whole emitter size.
    internal sealed class LineGeometryStorage
    {
        internal readonly List<Vector3> Positions=[],View=[];
        internal readonly List<EnbRenderColor32> Colors=[];
        internal readonly List<Vector2> Uv=[];
        internal readonly EnbW3dMutableIndexList Indices=[];
        internal readonly List<EnbRetailLineSubdivision.Node> Input=[];
        internal readonly EnbRetailLineSubdivision.Storage Subdivision=new();
        internal readonly EnbRetailLineCoalescing.Storage Coalescing=new();
        internal readonly UniformTriangleMetadata Metadata=new(0,default);
        private EnbW3dRenderMaterialPass? source,material;
        private readonly BindingRange bindings;
        internal LineGeometryStorage(){bindings=new(Indices);}
        private sealed class BindingRange(List<uint> indices):IReadOnlyList<EnbW3dRenderTriangleBinding>
        {
            internal EnbW3dRenderMaterialPass Source=null!;
            public int Count=>indices.Count/3;
            public EnbW3dRenderTriangleBinding this[int index] => (uint)index<(uint)Count
                ? Source.TriangleBindings[index] with {MaterialVertexIndex=(int)indices[index*3]}
                :throw new ArgumentOutOfRangeException(nameof(index));
            public IEnumerator<EnbW3dRenderTriangleBinding> GetEnumerator(){for(int i=0;i<Count;i++)yield return this[i];}
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
        }
        internal EnbW3dRenderMaterialPass Bind(EnbW3dRenderMaterialPass next)
        {
            if(!ReferenceEquals(source,next))
            {source=next;bindings.Source=next;material=next.Copy().SetTriangleBindings(bindings);}
            return material!;
        }
    }
    internal sealed record LineBindingPublication(EnbW3dRenderMaterialPass Source,
        int[] FirstIndices,EnbW3dRenderMaterialPass Result);
    internal sealed class RetainedLineBindings
    {
        public LineBindingPublication? Published;
    }

    private static EnbW3dRenderMaterialPass BindLineTopology(RetainedLineBindings owner,
        EnbW3dRenderMaterialPass source,List<uint> indices)
    {
        // Camera-dependent joining may change topology even at equal counts.
        // Publish the complete immutable binding together; another physical
        // line or concurrent invocation cannot overwrite a queued result.
        var state=Volatile.Read(ref owner.Published);
        int count=indices.Count/3;
        bool same=state is not null && ReferenceEquals(state.Source,source) && state.FirstIndices.Length==count;
        for(int i=0;same && i<count;i++)same=state!.FirstIndices[i]==(int)indices[i*3];
        if(same)return state!.Result;
        var first=new int[count];var bindings=new EnbW3dRenderTriangleBinding[count];
        for(int i=0;i<count;i++)
        {
            first[i]=(int)indices[i*3];
            var binding=source.TriangleBindings[i];
            bindings[i]=binding.MaterialVertexIndex==first[i]?binding:
                state is not null && i<state.Source.TriangleBindings.Count &&
                state.Source.TriangleBindings[i].HasSameStorage(binding) && i<state.FirstIndices.Length && state.FirstIndices[i]==first[i]
                ? state.Result.TriangleBindings[i] : binding with {MaterialVertexIndex=first[i]};
        }
        var result=(source).Copy().SetTriangleBindings(bindings);
        Volatile.Write(ref owner.Published,new(source,first,result));
        return result;
    }

    private static bool TryBuildConnectedLinePacket(EnbW3dRenderResourceKey key, ulong identity, ulong generation,
        IEnbParticleRenderOwner instance, EnbResolvedRenderCamera camera,
        EnbW3dLightEnvironment lighting, long now, List<ParticleState> live,
        out EnbW3dDrawTask? packet, out string? diagnostic, float particleSizeScale, long startTime, GeometryOwner? geometryOwner, EnbW3dTransientGeometryArena.Frame? frameStreams)
    {
        packet = null; diagnostic = null;
        using var positionScratch=ConstructionScratch<Vector3>.Rent(0);
        using var viewScratch=ConstructionScratch<Vector3>.Rent(0);
        using var colorScratch=ConstructionScratch<EnbRenderColor32>.Rent(0);
        using var uvScratch=ConstructionScratch<Vector2>.Rent(0);
        using var indexScratch=ConstructionScratch<uint>.Rent(0);
        var retained=geometryOwner is {ownedInvocation:true}?geometryOwner:null;
        var storage=retained?.Line;
        var positions=storage?.Positions??positionScratch.Values;var viewPositions=storage?.View??viewScratch.Values;
        var colors=storage?.Colors??colorScratch.Values;var uv=storage?.Uv??uvScratch.Values;var indices=storage?.Indices??indexScratch.Values;
        positions.Clear();viewPositions.Clear();colors.Clear();uv.Clear();indices.Clear();
        float halfWidth = instance.Definition.Size.Keys[0].Value * .5f * particleSizeScale; // buffer constructor -> renderer +8
        float tile = Math.Clamp(BitConverter.UInt32BitsToSingle(instance.Definition.LinePropertyWords[4]),0,256);
        float mergeAbort = BitConverter.UInt32BitsToSingle(instance.Definition.LinePropertyWords[3]);
        bool merge = (instance.Definition.LinePropertyWords[0] & 1) != 0;
        bool freeze = (instance.Definition.LinePropertyWords[0] & 2) != 0;
        uint mapping = instance.Definition.LinePropertyWords[0] >> 24;
        bool tiled = mapping is 1 or 2;
        int depth = (int)instance.Definition.LinePropertyWords[1];
        if((long)live.Count*(1<<depth)>EnbRetailParticleAdmission.MaximumParticles)
        {diagnostic="Subdivided particle line exceeds the local geometry resource budget";return false;}
        // 009BE813 initializes the clock at renderer creation; 009BEF17..93
        // advances both UV offsets in milliseconds and subtracts floor(offset).
        // Use the retained emitter epoch, never a particle birth or visibility run.
        float Scroll(int word)
        {
            double offset = (now - startTime) * (double)(BitConverter.UInt32BitsToSingle(instance.Definition.LinePropertyWords[word]) * .001f);
            return (float)(offset - Math.Floor(offset));
        }
        var scroll = new Vector2(Scroll(5), Scroll(6));
        AppendLineGeometry(live, camera, halfWidth, depth, freeze,
            BitConverter.UInt32BitsToSingle(instance.Definition.LinePropertyWords[2]), tiled, tile, scroll,
            positions, colors, uv, indices, viewPositions, merge, mergeAbort, mapping==1?0:1,storage);
        if(indices.Count==0) return true;
        if(!TryBuildMaterial(instance.Definition,instance.TextureLocation,indices.Count/3,
            out var material,out var semantics,out diagnostic,instance.RuntimeTextureName,instance.RetainedTexture,geometryOwner)) return false;
        material=storage is not null?storage.Bind(material):
            BindLineTopology(OwnerCache.Get(ref EnbPreparedParticleEmitter.Of(instance.Definition).LineBindings,instance.Definition,static _=>new RetainedLineBindings()),material,indices);
        var normals=ParticleNormals(instance.Definition,positions.Count);
        Vector3 minimum=new(float.PositiveInfinity), maximum=new(float.NegativeInfinity);
        foreach(var p in positions) { minimum=Vector3.Min(minimum,p); maximum=Vector3.Max(maximum,p); }
        var triangles=storage?.Metadata.Update(indices.Count/3,-camera.Basis.Forward)??new UniformTriangleMetadata(indices.Count/3,-camera.Basis.Forward);
        var borrowedPositions=frameStreams?.RentPositions(positions.Count);
        var borrowedView=frameStreams?.RentPositions(viewPositions.Count);
        var borrowedColors=frameStreams?.RentColors(colors.Count);
        if(borrowedPositions is not null)System.Runtime.InteropServices.CollectionsMarshal.AsSpan(positions).CopyTo(borrowedPositions.WriteSpan);
        if(borrowedView is not null)System.Runtime.InteropServices.CollectionsMarshal.AsSpan(viewPositions).CopyTo(borrowedView.WriteSpan);
        if(borrowedColors is not null)System.Runtime.InteropServices.CollectionsMarshal.AsSpan(colors).CopyTo(borrowedColors.WriteSpan);
        IReadOnlyList<Vector3> worldStream=borrowedPositions is not null?borrowedPositions:positions.ToImmutableArray();
        IReadOnlyList<Vector3> viewStream=borrowedView is not null?borrowedView:viewPositions.ToImmutableArray();
        IReadOnlyList<EnbRenderColor32> colorStream=borrowedColors is not null?borrowedColors:colors.ToImmutableArray();
        IReadOnlyList<Vector2> coordinates;
        IReadOnlyList<uint> indexStream;
        if(retained is not null)
        {
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(uv).CopyTo(retained.Uv.Prepare(uv.Count,0,[]));
            coordinates=retained.Uv;indexStream=indices;
            // Joining/coalescing writes the current index range even at equal
            // counts. Invalidate compiled topology explicitly, not by comparing
            // a published key or replacing a snapshot to force invalidation.
            if(retained.mesh is {} previous)previous.TopologyRevision++;
        }
        else {coordinates=uv.ToArray();indexStream=indices.ToArray();}
        packet=FinishParticleStreams(key,identity,generation,instance,lighting,now,material,semantics,
            worldStream,normals,colorStream,coordinates,indexStream,triangles,minimum,maximum,
            viewPositions:viewStream,materialOwner:retained,renderInstancePath:geometryOwner?.RenderInstancePath);
        return true;
    }

    private static void AppendLineGeometry(List<ParticleState> live, EnbResolvedRenderCamera camera,
        float halfWidth, int depth, bool freeze, float noise, bool tiled, float tile, Vector2 scroll,
        List<Vector3> positions, List<EnbRenderColor32> colors, List<Vector2> uv, List<uint> indices,
        List<Vector3> viewPositions, bool merge, float mergeAbort, float upperU,LineGeometryStorage? storage=null)
    {
        EnbRenderCameraMatrixCompiler.TryCreateWorldToCanonicalView(camera, out var view);
        //00921F94 omits FPU_PRESERVE; the installed D3D8 creation path
        // resets PC to24 (dependency1004D079..091).008D3260 transforms
        // original points with the saved view before subdivision.
        const EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24;
        Vector3 ToView(Vector3 p) => EnbRetailLineCoalescing.TransformPoint(p, view, precision);
        Vector3 ToWorld(Vector3 p) => camera.Position + camera.Basis.Right * p.X + camera.Basis.Up * p.Y - camera.Basis.Forward * p.Z;
        for (int start = 0; start < live.Count;)
        {
            int end = start + 1;
            while (end < live.Count && live[end].EmissionRun == live[start].EmissionRun) end++;
            if (end - start >= 2)
            {
                var input=storage?.Input??new List<EnbRetailLineSubdivision.Node>(end-start);
                input.Clear();
                for(int i=0;i<end-start;i++)
                {
                    var p=live[start+i];
                    input.Add(new(ToView(p.Center),i){Color=p.LineColor,V=tiled?i*tile:0});
                }
                int draws = checked((input.Count - 1) * ((1 << depth) - 1) * 3);
                uint counter = freeze ? 0 : EnbRetailLineSubdivision.ReserveNonFrozenDraws(draws);
                EnbRetailLineSubdivision.Polyline? detached=null;
                if(storage is null)detached=EnbRetailLineSubdivision.BuildBatches(input,depth,noise,freeze,counter:counter,precision:precision);
                else EnbRetailLineSubdivision.BuildOwned(input,depth,noise,freeze,counter,precision,storage.Subdivision);
                // The native renderer has an uninitialized local candidate,
                // contaminated by clock/pointer/caller stack words. Correct
                //that bug by tracking whether this call produced a candidate;
                //only a projection lacking one leaves its records unmerged.
                Vector3 retainedRay = default;
                bool hasRetainedRay = false;
                int batchCount=storage?.Subdivision.Count??detached!.Batches.Count;
                for(int batchIndex=0;batchIndex<batchCount;batchIndex++)
                {
                    IReadOnlyList<EnbRetailLineSubdivision.Node> nodes=storage is null?detached!.Batches[batchIndex].Nodes:storage.Subdivision.Batches[batchIndex].Active;
                    var scratch = EnbRetailLineCoalescing.Construct(nodes, halfWidth, retainedRay, precision,storage?.Coalescing);
                    var intersections = merge
                        ? EnbRetailLineCoalescing.Compact(scratch.Upper, scratch.Lower, scratch.Planes,
                            halfWidth, mergeAbort, retainedRay, precision, hasRetainedRay,storage?.Coalescing,scratch.Count)
                        : new EnbRetailLineCoalescing.Result(scratch.Upper, scratch.Lower){UpperCount=scratch.Count,LowerCount=scratch.Count};
                    retainedRay = intersections.RetainedCandidateRay;
                    hasRetainedRay = intersections.HasRetainedCandidateRay;
                    // Mapping0 and2 both use U=0/1 (009BF34D..354).
                    var geometry = EnbRetailLineCoalescing.Emit(nodes, intersections, 0, upperU, scroll, precision,storage?.Coalescing);
                    uint first = (uint)positions.Count;
                    for(int vertexIndex=0;vertexIndex<geometry.VertexCount;vertexIndex++)
                    {
                        var vertex=geometry.Vertices[vertexIndex];
                        positions.Add(ToWorld(vertex.Position)); uv.Add(vertex.Uv);
                        viewPositions.Add(vertex.Position);
                        uint c = vertex.Argb;
                        colors.Add(new((byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)(c >> 24)));
                    }
                    for(int i=0;i<geometry.IndexCount;i++)indices.Add(first+geometry.Indices[i]);
                }
            }
            start = end;
        }
    }
}

