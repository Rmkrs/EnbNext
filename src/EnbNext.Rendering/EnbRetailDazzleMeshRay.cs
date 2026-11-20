using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Diagnostics;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>Private collision projection of a retained mesh. 00940530 consumes
/// the base model vertices, including for skinned meshes; render deformation
/// and material alpha are not collision inputs.</summary>
public sealed class EnbRetailDazzleMeshRay
{
    private readonly bool enabled,billboard,usePlaneOnlyRange;
    private readonly uint facingFlags;
    private bool facingInverseValid;
    private Vector3 facingPosition,facingTarget;
    private readonly EnbCurrentRetailMeshAabTree? authoredTree;
    private readonly (Vector3 Center,Vector3 Extent)[] treeBounds;
    private readonly string diagnosticName;
    private readonly EnbRetailDazzleCollision.Triangle[] triangles;
    private readonly EnbRetailTrianglePlaneBlocks? planeBlocks;
    private readonly EnbRetailTrianglePlaneBlocks?[]? treePlaneBlocks;
    internal long PlaneRejectedTriangles {get;private set;}
    private readonly object gate=new();
    private Matrix4x4 inverseWorld;
    private bool inverseWorldValid;
    private readonly Stack<int> pendingNodes=new();
    private readonly float[] retainedInverse=new float[12];
    private readonly EnbCurrentRetailMeshGeometryState? sourceGeometry;
    private readonly EnbCurrentRetailMeshAabTree? sourceTree;
    [ThreadStatic] private static bool rayNormalizationValid;
    [ThreadStatic] private static Vector3 normalizedDelta, normalizedDirection;
    [ThreadStatic] private static float normalizedSquare;
    private readonly List<OcclusionPose> occlusionPoses=[];
    private long occlusionAccess;
    private sealed class OcclusionPose
    {
        public Matrix4x4 World;
        public long Access;
        public int Count, Next;
        public readonly OcclusionQuery[] Queries=new OcclusionQuery[16];
    }
    internal bool UseUncachedOcclusionQueries { get; set; }
    internal int OcclusionCacheHits { get; private set; }
    private readonly record struct OcclusionQuery(Vector3 Start, Vector3 End, float InputFraction,
        float Fraction, bool StartInside);

    //48E9D0 can generate a tree on a shared registered model after another
    //instance has already acquired a collision projection of that model.
    public bool IsCurrentGeometry => ReferenceEquals(sourceTree,sourceGeometry?.Tree);
    public Vector3 ModelSphereCenter { get; }
    public float ModelSphereRadius { get; }

    public EnbRetailDazzleMeshRay(EnbW3dRenderMesh mesh,float modelScale,uint? retainedMask=null,bool? hidden=null,uint queryMask=16,float? remainingGeometryScale=null,Vector3? vertexScale=null,bool usePlaneBlocks=false,bool useTreePlaneBlocks=false,bool retainTriangleOperands=false,bool usePlaneOnlyRange=false)
    {
        this.usePlaneOnlyRange=usePlaneOnlyRange;
        diagnosticName=mesh.Name;
        sourceGeometry=mesh.CurrentRetailLoadedGeometry;sourceTree=sourceGeometry?.Tree;
        uint flags=EnbCurrentRetailMeshGeometryLoader.TranslateInitialFlags(mesh.GeometryVersion,mesh.Attributes,1);
        if(mesh.BoneIndices.Count!=0)flags|=0x400;
        // 009BAA00 loads0090 before009BCC40 drops it for skinned models.
        // 0092AE10 selects the retained tree, not the linear triangle loop.
        var tree=(flags&0x400)==0 && mesh.AuthoredAabTreePayload is {} payload
            ?EnbCurrentRetailMeshAabTree.LoadOwnedAuthored(payload.ToArray()):null;
        var geometry=mesh.CurrentRetailLoadedGeometry?.CopyModel()??new EnbCurrentRetailMeshGeometryState(mesh.Positions.ToArray(),new Vector3[mesh.Positions.Count],
            mesh.Indices.Select(index=>checked((ushort)index)).ToArray(),[],mesh.BoundingBoxMin,mesh.BoundingBoxMax,
            mesh.BoundingSphereCenter,mesh.BoundingRadius,flags,tree);
        //This is a private query projection. A nonuniform morph may rebuild
        //its acceleration tree, whose partition does not change the tested
        //triangle set. It must not advance the model construction CRT stream.
        uint unusedTreeSeed=0;
        float remainingScale=remainingGeometryScale??modelScale/mesh.CurrentRetailLoadedGeometryScale;
        var scale=(vertexScale??Vector3.One)*remainingScale;
        if(scale!=Vector3.One)geometry.Scale(scale,ref unusedTreeSeed);
        ModelSphereCenter=geometry.Center;ModelSphereRadius=geometry.Radius;
        authoredTree=geometry.Tree;
        treeBounds=authoredTree is null?[]:new (Vector3,Vector3)[authoredTree.Nodes.Count];
        if(authoredTree is not null)
        {
            using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
            for(int i=0;i<treeBounds.Length;i++)
            {
                var node=authoredTree.Nodes[i];Vector3 center=default,extent=default;
                for(int axis=0;axis<3;axis++)
                {
                    center[axis]=(float)(((R)node.Maximum[axis]+node.Minimum[axis])*.5f);
                    extent[axis]=(float)(((R)node.Maximum[axis]-node.Minimum[axis])*.5f);
                }
                treeBounds[i]=(center,extent);
            }
        }
        enabled=((retainedMask??EnbRetailDazzleCollision.ConfiguredMeshMask(mesh))&queryMask)!=0 &&
            !(hidden??false);
        billboard=(geometry.Flags&0xa00)!=0;
        facingFlags=geometry.Flags&0xa00;
        triangles=new EnbRetailDazzleCollision.Triangle[mesh.Indices.Count/3];
        for(int i=0;i<triangles.Length;i++)
        {
            var a=geometry.Positions[(int)mesh.Indices[i*3]];
            var b=geometry.Positions[(int)mesh.Indices[i*3+1]];
            var c=geometry.Positions[(int)mesh.Indices[i*3+2]];
            var plane=EnbCurrentRetailMeshGeometryRuntime.CalculateTrianglePlane(a,b,c);
            triangles[i]=new(a,b,c,new(plane.X,plane.Y,plane.Z),retainTriangleOperands);
        }
        if(usePlaneBlocks && System.Numerics.Vector.IsHardwareAccelerated && authoredTree is null && !billboard &&
            triangles.Length>=EnbRetailTrianglePlaneBlocks.Width)
            planeBlocks=new(triangles);
        if(useTreePlaneBlocks && System.Numerics.Vector.IsHardwareAccelerated && authoredTree is not null && !billboard)
        {
            treePlaneBlocks=new EnbRetailTrianglePlaneBlocks?[authoredTree.Nodes.Count];
            for(int nodeIndex=0;nodeIndex<authoredTree.Nodes.Count;nodeIndex++)
            {
                var node=authoredTree.Nodes[nodeIndex];
                if((node.First&0x80000000)==0 || node.Second<EnbRetailTrianglePlaneBlocks.Width)continue;
                long first=node.First&0x7fffffff, count=node.Second;
                // Malformed retained data keeps its original on-demand failure
                // path. Registration must not discover new collision failures.
                if(first+count>authoredTree.PolygonIndices.Count)continue;
                bool valid=true;
                for(int i=0;i<count;i++)if(authoredTree.PolygonIndices[(int)first+i]>=triangles.Length){valid=false;break;}
                if(!valid)continue;
                var ordered=new EnbRetailDazzleCollision.Triangle[(int)count];
                for(int i=0;i<ordered.Length;i++)ordered[i]=triangles[authoredTree.PolygonIndices[(int)first+i]];
                treePlaneBlocks[nodeIndex]=new(ordered);
            }
        }
    }

    public (float Fraction,bool StartInside) CastSegment(Matrix4x4 rigidWorld,Vector3 start,Vector3 end,
        float fraction=1,bool startInside=false)
        => CastCore(rigidWorld,start,end,fraction,startInside,false);

    /// <summary>0075DEE0 consumes only fraction&lt;1 and then stops the sample.
    /// An accepted mesh triangle cannot be undone by later collision leaves.
    /// This consumer may stop there; nearest-hit callers retain CastSegment.</summary>
    public (float Fraction,bool StartInside) CastOcclusionSegment(Matrix4x4 rigidWorld,Vector3 start,Vector3 end,
        float fraction=1,bool startInside=false)
    {
        // This projection owns an immutable copy of the mesh. Repeating the
        // identical segment against the identical rigid pose is a pure query;
        // it need not repeat the triangle walk. Keep one sun's sixteen samples,
        // not a time-based visibility result. Moving either endpoint or owner
        // still executes the original path, including its exact fraction.
        // 009405DA..00940662 builds facing geometry from the copied owner
        // translation and this ray's endpoints/direction. Those inputs are
        // already in the key; it reads no external camera or frame clock.
        // Misses still use the literal facing path in CastCore.
        if (!enabled || startInside || UseUncachedOcclusionQueries || EnbRetailDazzleCollision.UseLiteralTriangleOracle)
            return CastCore(rigidWorld,start,end,fraction,startInside,true);
        lock(gate)
        {
            // Geometry projections are shared by separate scene instances.
            // A small bounded pose set avoids those instances evicting one
            // another on every sample. Eviction affects work, never results.
            OcclusionPose? pose=null;
            foreach(var candidate in occlusionPoses)
                if(SameBits(rigidWorld,candidate.World)){pose=candidate;break;}
            if(pose is null)
            {
                if(occlusionPoses.Count<4){pose=new();occlusionPoses.Add(pose);}
                else
                {
                    pose=occlusionPoses[0];
                    foreach(var candidate in occlusionPoses)
                        if(candidate.Access<pose.Access)pose=candidate;
                }
                pose.World=rigidWorld;pose.Count=pose.Next=0;
            }
            pose.Access=++occlusionAccess;
                for (int i=0;i<pose.Count;i++)
                {
                    ref readonly var query=ref pose.Queries[i];
                    if (SameBits(start,query.Start)&&SameBits(end,query.End)&&SameBits(fraction,query.InputFraction))
                    {
                        OcclusionCacheHits++;
                        if(EnbDazzleVisibilityProfile.Active is {} profile)profile.CachedMeshQueries++;
                        return(query.Fraction,query.StartInside);
                    }
                }
            var result=CastCore(rigidWorld,start,end,fraction,startInside,true);
            pose.Queries[pose.Next]=new(start,end,fraction,result.Fraction,result.StartInside);
            pose.Next=(pose.Next+1)%pose.Queries.Length;
            pose.Count=Math.Min(pose.Count+1,pose.Queries.Length);
            return result;
        }
    }

    private static bool SameBits<T>(T left,T right) where T:unmanaged
        => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref left,1)).SequenceEqual(
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref right,1)));

    private (float Fraction,bool StartInside) CastCore(Matrix4x4 rigidWorld,Vector3 start,Vector3 end,
        float fraction,bool startInside,bool occlusionOnly)
    {
        if(!enabled||startInside)return(fraction,startInside);
        var profile=EnbDazzleVisibilityProfile.Active;
        long profileStart=profile is null?0:Stopwatch.GetTimestamp();
        int tested=0,nodes=0;
        try
        {
        lock(gate)
        {
            using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
            bool reuseInverse=!billboard&&inverseWorldValid&&SameBits(rigidWorld,inverseWorld);
            Vector3 delta=end-start;
            NormalizeWorldRay(delta,true,out float square,out Vector3 normal);
            {
                // The sixteen visibility samples share the same rigid owner.
                // Keep the raw retail inverse, including signed-zero bits;
                // a changed owner invalidates it.
                if(!reuseInverse)
                {
                    inverseWorldValid=false;
                    if(billboard)
                    {
                        // Facing meshes use only the owner translation and either the
                        // translation minus the ray normal (0200) or the ray start (0800).
                        var position=new Vector3(rigidWorld.M41,rigidWorld.M42,rigidWorld.M43);
                        var target=(facingFlags&0x200)!=0 ? position-normal : start;
                        if(!facingInverseValid || !SameBits(position,facingPosition) || !SameBits(target,facingTarget))
                        {
                            InvertRigid(EnbRetailCameraLookAt.BetweenPoints(position,target),retainedInverse);
                            facingPosition=position;facingTarget=target;facingInverseValid=true;
                        }
                    }
                    else InvertRigid(rigidWorld,retainedInverse);
                    inverseWorld=rigidWorld;inverseWorldValid=true;
                }
                TransformRay(retainedInverse,start,end,normal,out var origin,out var endpoint,out var direction,out var normalized);
                bool singleRay=EnbRetailDazzleCollision.SingleTriangleRange(origin)&&EnbRetailDazzleCollision.SingleTriangleRange(direction);
                bool planeRay=singleRay || usePlaneOnlyRange && EnbRetailDazzleCollision.SinglePlaneRange(origin)&&EnbRetailDazzleCollision.SinglePlaneRange(direction);
                if(planeBlocks is not null && planeRay && !EnbRetailDazzleCollision.UseLiteralTriangleOracle)
                {
                    for(int block=0;block<planeBlocks.Count;block++)
                    {
                        var candidates=planeBlocks.Candidates(block,origin,direction,usePlaneOnlyRange,singleRay);
                        int first=block*EnbRetailTrianglePlaneBlocks.Width;
                        int count=Math.Min(EnbRetailTrianglePlaneBlocks.Width,triangles.Length-first);
                        if(System.Numerics.Vector.EqualsAll(candidates,Vector<int>.Zero))
                        {PlaneRejectedTriangles+=count;continue;}
                        for(int lane=0;lane<count;lane++)
                        {
                            if(candidates[lane]==0){PlaneRejectedTriangles++;continue;}
                            tested++;
                            if(triangles[first+lane].Intersect(origin,direction,singleRay,ref fraction)&&occlusionOnly&&!billboard)
                                return(fraction,false);
                        }
                    }
                }
                else if(authoredTree is null)
                {
                    foreach(var triangle in triangles)
                    {
                        tested++;
                        if(triangle.Intersect(origin,direction,singleRay,ref fraction)
                            && occlusionOnly&&!billboard)break;
                    }
                }
                else
                {
                    //009B38F0 tests authored bounds then first/second children;
                    //009B3DC0 uses the leaf's polygon-index slice. Keep the
                    //bounds classifier and precomputed triangle planes.
                    var pending=pendingNodes;pending.Clear();pending.Push(0);
                    while(pending.TryPop(out int index))
                    {
                        nodes++;
                        var node=authoredTree.Nodes[index];
                        var bounds=treeBounds[index];
                        if(EnbRetailDazzleCollision.ClassifyRayBounds(bounds.Center,bounds.Extent,origin,endpoint,direction,normalized)==1)continue;
                        if((node.First&0x80000000)==0)
                        {pending.Push(checked((int)node.Second));pending.Push(checked((int)node.First));continue;}
                        int first=checked((int)(node.First&0x7fffffff));
                        var leafBlocks=singleRay && !EnbRetailDazzleCollision.UseLiteralTriangleOracle ? treePlaneBlocks?[index] : null;
                        Vector<int> leafCandidates=default;
                        for(int i=0;i<unchecked((int)node.Second);i++)
                        {
                            tested++;
                            if(leafBlocks is not null)
                            {
                                int lane=i%EnbRetailTrianglePlaneBlocks.Width;
                                if(lane==0)leafCandidates=leafBlocks.Candidates(i/EnbRetailTrianglePlaneBlocks.Width,origin,direction);
                                if(leafCandidates[lane]==0){tested--;PlaneRejectedTriangles++;continue;}
                            }
                            var triangle=triangles[authoredTree.PolygonIndices[first+i]];
                            if(triangle.Intersect(origin,direction,singleRay,ref fraction)&&occlusionOnly&&!billboard)
                                return(fraction,false);
                        }
                    }
                }
                return(fraction,false);
            }
        }
        }
        finally {profile?.Mesh(diagnosticName,Stopwatch.GetTimestamp()-profileStart,tested,nodes,false);}
    }

    internal static void NormalizeWorldRay(Vector3 delta,bool reuse,out float square,out Vector3 normal)
    {
        // Every mesh visited by one visibility sample receives the same world
        // segment. Its normalization is independent of the mesh and transform.
        // A bitwise key retains signed-zero semantics; state is thread-local.
        if(reuse&&rayNormalizationValid&&
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref delta,1)).SequenceEqual(
                MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref normalizedDelta,1))))
        {square=normalizedSquare;normal=normalizedDirection;return;}
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        square=(float)((R)delta.X*delta.X+(R)delta.Y*delta.Y+(R)delta.Z*delta.Z);
        R inverse=square==0?0:EnbRetailLineCoalescing.ReciprocalSquareRoot(square);
        normal=square==0?delta:new((float)((R)delta.X*inverse),(float)((R)delta.Y*inverse),(float)((R)delta.Z*inverse));
        if(reuse&&float.IsFinite(square)&&float.IsFinite(normal.X)&&float.IsFinite(normal.Y)&&float.IsFinite(normal.Z))
        {normalizedDelta=delta;normalizedSquare=square;normalizedDirection=normal;rayNormalizationValid=true;}
    }

    /// <summary>Retail rigid inverse: the transposed basis and the negated, back-rotated translation.</summary>
    internal static void InvertRigid(Matrix4x4 m,Span<float> inverse)
    {
        inverse[0]=m.M11;inverse[1]=m.M12;inverse[2]=m.M13;
        inverse[4]=m.M21;inverse[5]=m.M22;inverse[6]=m.M23;
        inverse[8]=m.M31;inverse[9]=m.M32;inverse[10]=m.M33;
        inverse[3]=-((m.M13*m.M43+m.M12*m.M42)+m.M11*m.M41);
        inverse[7]=-((m.M23*m.M43+m.M21*m.M41)+m.M22*m.M42);
        inverse[11]=-((m.M33*m.M43+m.M31*m.M41)+m.M32*m.M42);
    }

    /// <summary>The world segment and its direction in the mesh's local space, in retail's
    /// summation orders.</summary>
    internal static void TransformRay(ReadOnlySpan<float> m,Vector3 start,Vector3 end,Vector3 normal,
        out Vector3 origin,out Vector3 endpoint,out Vector3 delta,out Vector3 normalized)
    {
        origin=new(m[0]*start.X+m[1]*start.Y+m[2]*start.Z+m[3],
            m[4]*start.X+start.Z*m[6]+start.Y*m[5]+m[7],
            start.Z*m[10]+m[9]*start.Y+m[8]*start.X+m[11]);
        endpoint=new(end.Y*m[1]+end.X*m[0]+m[2]*end.Z+m[3],
            end.Z*m[6]+end.Y*m[5]+end.X*m[4]+m[7],
            end.Z*m[10]+end.Y*m[9]+end.X*m[8]+m[11]);
        delta=endpoint-origin;
        normalized=new(normal.Y*m[1]+m[0]*normal.X+m[2]*normal.Z,
            m[4]*normal.X+normal.Z*m[6]+normal.Y*m[5],
            normal.Z*m[10]+normal.Y*m[9]+m[8]*normal.X);
    }
}
