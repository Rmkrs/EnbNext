using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public sealed class EnbCameraCollider(ulong Owner, EnbW3dAxisAlignedBounds LocalBounds,
    Matrix4x4 World, IReadOnlyList<EnbCameraCollisionMesh> Meshes)
{
    public ulong Owner {get;set;}=Owner;
    public EnbW3dAxisAlignedBounds LocalBounds {get;set;}=LocalBounds;
    public Matrix4x4 World {get;set;}=World;
    public IReadOnlyList<EnbCameraCollisionMesh> Meshes {get;set;}=Meshes;
    // Independently registered effects are distinct RenderObjs (6EF200/942140).
    public bool ExcludeWithOwner {get;set;}=true;
    public IReadOnlyList<EnbCameraCollisionBox> Boxes {get;set;}=[];
    public EnbCameraCollider Copy()=>new(Owner,LocalBounds,World,Meshes){ExcludeWithOwner=ExcludeWithOwner,Boxes=Boxes};
    internal IEnumerable<object> OrderedLeaves=>Meshes.Select(m=>(m.TraversalOrder,Leaf:(object)m))
        .Concat(Boxes.Select(b=>(b.TraversalOrder,Leaf:(object)b))).OrderBy(x=>x.TraversalOrder).Select(x=>x.Leaf);
}

public sealed class EnbCameraCollisionBox(Vector3 Center,Vector3 Extents,Vector3 X,Vector3 Y,Vector3 Z)
{
    public Vector3 Center {get;set;}=Center;
    public Vector3 Extents {get;set;}=Extents;
    public Vector3 X {get;set;}=X;
    public Vector3 Y {get;set;}=Y;
    public Vector3 Z {get;set;}=Z;
    public int TraversalOrder {get;set;}
    public EnbCameraCollisionBox Copy()=>new(Center,Extents,X,Y,Z){TraversalOrder=TraversalOrder};
}

public sealed class EnbCameraCollisionMesh(EnbCameraCollisionGeometry Geometry,Matrix4x4 World)
{
    public EnbCameraCollisionGeometry Geometry {get;set;}=Geometry;
    public Matrix4x4 World {get;set;}=World;
    // A09840 returns after the first member model reporting a hit.
    public object? FirstHitGroup {get;set;}
    public int TraversalOrder {get;set;}
    public EnbCameraCollisionMesh Copy()=>new(Geometry,World){FirstHitGroup=FirstHitGroup,TraversalOrder=TraversalOrder};
}

/// <summary>940830/92AE70 sweep retained base vertices, independently of skin,
/// draw LOD and render/hierarchy visibility flags.</summary>
public sealed class EnbCameraCollisionGeometry
{
    public IReadOnlyList<Vector3> Positions {get;}
    public IReadOnlyList<uint> Indices {get;}
    public uint Mask {get;}
    private readonly EnbCameraCollisionTree? _queryTree;
    private readonly EnbCurrentRetailMeshGeometryState? _sourceGeometry;
    private readonly ulong _vectorRevision;
    internal EnbCameraCollisionTree? QueryTree => _sourceGeometry is null || _sourceGeometry.VectorRevision==_vectorRevision ? _queryTree : null;
    public EnbCameraCollisionGeometry(EnbRetailSpecialMaterialGeometry geometry,uint mask)
    {Positions=geometry.Positions;Indices=geometry.Indices.Select(i=>(uint)i).ToArray();Mask=mask;}
    public EnbCameraCollisionGeometry(EnbW3dRenderMesh mesh,float scale=1,uint? mask=null)
    {
        _sourceGeometry=mesh.CurrentRetailLoadedGeometry;
        _vectorRevision=_sourceGeometry?.VectorRevision??0;
        Mask=mask??EnbRetailDazzleCollision.LoadedMeshMask(mesh);
        Indices=mesh.Indices;
        float pending=scale/mesh.CurrentRetailLoadedGeometryScale;
        var positions=mesh.CurrentRetailLoadedGeometry?.Positions??mesh.Positions;
        Positions=pending==1?positions:positions.Select(p=>p*pending).ToArray();
        // 92AEA0/9B3CC0 select the retained model tree before exact triangle
        // overlap. Reuse its partition without consuming construction RNG.
        var tree=mesh.CurrentRetailLoadedGeometry?.Tree;
        if(tree is null && mesh.BoneIndices.Count==0 && mesh.AuthoredAabTreePayload is {} payload)
            tree=EnbCurrentRetailMeshAabTree.LoadOwnedAuthored(payload.ToArray());
        if(tree is not null)_queryTree=EnbCameraCollisionTree.Create(tree,Positions,Indices);
    }
}

/// <summary>007998B0/0075FB00: sweep the camera near-plane box from the ship.</summary>
public static class EnbShipCameraCollision
{
    // Offline differential control; the linear consumer is also required for models without a tree.
    [ThreadStatic] internal static bool UseLinearOverlapQueries;
    //0063E930 queries only the room RenderObj with mask2 and a world-axis
    //half-unit box. It does not use75FB00's scene broadphase/ship exclusion.
    public static float SweepStationRoom(Vector3 origin,Vector3 movement,EnbCameraCollider room)
    {
        float fraction=1;var extent=new Vector3(.5f);
        var hitGroups=new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach(var leaf in room.OrderedLeaves)
        {
            if(leaf is EnbCameraCollisionBox box)
            {fraction=MathF.Min(fraction,EnbCameraBoxCollision.Sweep(extent,movement,box.Center-origin,box.Extents,box.X,box.Y,box.Z));continue;}
            var packet=(EnbCameraCollisionMesh)leaf;
            if((packet.Geometry.Mask&2)==0 || packet.FirstHitGroup is {} group && hitGroups.Contains(group))continue;
            var positions=packet.Geometry.Positions;var indices=packet.Geometry.Indices;bool hit=false;
            for(int i=0;i+2<indices.Count;i+=3)
            {
                var a=Vector3.Transform(positions[(int)indices[i]],packet.World)-origin;
                var b=Vector3.Transform(positions[(int)indices[i+1]],packet.World)-origin;
                var c=Vector3.Transform(positions[(int)indices[i+2]],packet.World)-origin;
                float candidate=Sweep(a,b,c,extent,movement);
                hit|=candidate<1 && candidate<=fraction;fraction=MathF.Min(fraction,candidate);
            }
            if(hit && packet.FirstHitGroup is {} hitGroup)hitGroups.Add(hitGroup);
        }
        return fraction;
    }
    /// <summary>796920 calls 75FB00 with the ship box at two candidate endpoints,
    /// a million-unit direction sweep, exclusion of its own RenderObj and flag=true.</summary>
    public static float SweepTakeoff(EnbW3dAxisAlignedBounds shipBounds, Quaternion orientation,
        Vector3 start, Vector3 end, Vector3 movement, ulong owner,
        IReadOnlyList<EnbCameraCollider> colliders, EnbRetailTerrain? terrain)
    {
        var inverse = Quaternion.Conjugate(orientation);
        var center = start + Vector3.Transform(shipBounds.Center, orientation);
        Vector3 Local(Vector3 p) => Vector3.Transform(p - center, inverse);
        Vector3 Axis(Vector3 p)=>Vector3.Transform(p,inverse);
        var localMovement = Vector3.Transform(movement, inverse);
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2)
        {
            var offset = Vector3.Transform(shipBounds.Center + shipBounds.Extents * new Vector3(x,y,z), orientation);
            min = Vector3.Min(min, Vector3.Min(start + offset, end + offset));
            max = Vector3.Max(max, Vector3.Max(start + offset, end + offset));
        }
        float fraction = 1;
        foreach (var collider in colliders)
        {
            if (collider.ExcludeWithOwner && collider.Owner == owner) continue;
            // 75FB00 admits the RenderObj by the union of the endpoint boxes,
            // then sends the long sweep to that object's collision consumer.
            if(!IntersectsCandidate(collider,(min+max)*.5f,(max-min)*.5f))continue;
            var hitGroups=new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var leaf in collider.OrderedLeaves)
            {
                if(leaf is EnbCameraCollisionBox box)
                {fraction=MathF.Min(fraction,EnbCameraBoxCollision.Sweep(shipBounds.Extents,localMovement,Local(box.Center),box.Extents,Axis(box.X),Axis(box.Y),Axis(box.Z)));continue;}
                var packet=(EnbCameraCollisionMesh)leaf;
                if(packet.FirstHitGroup is {} group && hitGroups.Contains(group))continue;
                if ((packet.Geometry.Mask & 0x10) == 0) continue;
                var positions = packet.Geometry.Positions;
                var indices = packet.Geometry.Indices;
                bool hit=false;
                for (int i = 0; i + 2 < indices.Count; i += 3)
                {
                    var a = Vector3.Transform(positions[(int)indices[i]], packet.World);
                    var b = Vector3.Transform(positions[(int)indices[i+1]], packet.World);
                    var c = Vector3.Transform(positions[(int)indices[i+2]], packet.World);
                    float candidate=Sweep(Local(a), Local(b), Local(c), shipBounds.Extents, localMovement);
                    hit|=candidate<1&&candidate<=fraction;
                    fraction=MathF.Min(fraction,candidate);
                }
                if(hit&&packet.FirstHitGroup is {} hitGroup)hitGroups.Add(hitGroup);
            }
        }
        if (terrain is not null)
        {
            // 46C6E0 -> 4654E0 bypasses the scene endpoint broadphase and
            // visits the full OBB displacement. Restrict only by the height
            // envelope: triangles outside it cannot intersect this sweep.
            var basis = Matrix4x4.CreateFromQuaternion(orientation);
            var extent = Vector3.Abs(new Vector3(basis.M11,basis.M12,basis.M13))*shipBounds.Extents.X
                + Vector3.Abs(new Vector3(basis.M21,basis.M22,basis.M23))*shipBounds.Extents.Y
                + Vector3.Abs(new Vector3(basis.M31,basis.M32,basis.M33))*shipBounds.Extents.Z;
            float lowHeight=terrain.Heights.Min()*terrain.VerticalScale;
            float highHeight=terrain.Heights.Max()*terrain.VerticalScale;
            if(lowHeight>highHeight)(lowHeight,highHeight)=(highHeight,lowHeight);
            float enter=0,leave=1;
            if(movement.Z==0)
            {
                if(center.Z+extent.Z<lowHeight || center.Z-extent.Z>highHeight)return fraction;
            }
            else
            {
                float a=(lowHeight-extent.Z-center.Z)/movement.Z;
                float b=(highHeight+extent.Z-center.Z)/movement.Z;
                if(a>b)(a,b)=(b,a);
                enter=MathF.Max(0,a);leave=MathF.Min(1,b);
                if(enter>leave)return fraction;
            }
            min=Vector3.Min(center+movement*enter,center+movement*leave)-extent;
            max=Vector3.Max(center+movement*enter,center+movement*leave)+extent;
            float cell = terrain.HorizontalScale / terrain.Width;
            int x0 = (int)(min.X/cell + terrain.Width/2f-1), x1 = (int)(max.X/cell + terrain.Width/2f+1);
            int y0 = (int)(min.Y/cell + terrain.Width/2f-1), y1 = (int)(max.Y/cell + terrain.Width/2f+1);
            for (int y=y0; y<=y1; y++) for (int x=x0; x<=x1; x++)
            {
                var a=Local(terrain.Position(x,y)); var b=Local(terrain.Position(x+1,y));
                var c=Local(terrain.Position(x+1,y+1)); var d=Local(terrain.Position(x,y+1));
                fraction=MathF.Min(fraction,MathF.Min(Sweep(a,b,c,shipBounds.Extents,localMovement),Sweep(a,c,d,shipBounds.Extents,localMovement)));
            }
        }
        return fraction;
    }
    public static EnbResolvedRenderCamera Resolve(EnbResolvedRenderCamera camera, Vector3 ship,
        float shipRadius, ulong shipOwner, bool keepShipInView, IReadOnlyList<EnbCameraCollider> colliders,
        EnbRetailTerrain? terrain = null)
    {
        var delta=camera.Position-ship;
        float distance=delta.Length();
        if(distance<1e-5f)return camera;
        float near=camera.Projection.ClipRange.NearPlane;
        var extent=new Vector3(near/camera.Projection.Matrix.M11,near/camera.Projection.Matrix.M22,.01f);
        var origin=ship+camera.Basis.Forward*near;
        var end=origin+delta;
        float sphere=extent.Length(),fraction=1;
        var basis=camera.Basis;
        var worldExtent=Vector3.Abs(basis.Right)*extent.X+Vector3.Abs(basis.Up)*extent.Y+Vector3.Abs(basis.Forward)*extent.Z;
        var candidateMin=Vector3.Min(origin,end)-worldExtent;
        var candidateMax=Vector3.Max(origin,end)+worldExtent;
        Vector3 Local(Vector3 p)=>new(Vector3.Dot(p,basis.Right),Vector3.Dot(p,basis.Up),Vector3.Dot(p,basis.Forward));
        var movement=Local(delta);
        foreach(var collider in colliders)
        {
            if((collider.ExcludeWithOwner && collider.Owner==shipOwner) || !Matrix4x4.Invert(collider.World,out var inverse))continue;
            var localEnd=Vector3.Transform(end,inverse)-collider.LocalBounds.Center;
            var expansion=new Vector3(new Vector3(inverse.M11,inverse.M21,inverse.M31).Length(),
                new Vector3(inverse.M12,inverse.M22,inverse.M32).Length(),new Vector3(inverse.M13,inverse.M23,inverse.M33).Length())*sphere;
            var bounds=collider.LocalBounds.Extents+expansion;
            // Disabled still keeps the near plane outside the enclosing object.
            if(!keepShipInView && (MathF.Abs(localEnd.X)>bounds.X || MathF.Abs(localEnd.Y)>bounds.Y || MathF.Abs(localEnd.Z)>bounds.Z))continue;
            // 75FB00's +DC is triangle overlap with the endpoint-box union,
            // not a segment test against expanded object bounds.
            if(!IntersectsCandidate(collider,(candidateMin+candidateMax)*.5f,(candidateMax-candidateMin)*.5f))continue;
            var hitGroups=new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach(var leaf in collider.OrderedLeaves)
            {
                if(leaf is EnbCameraCollisionBox box)
                {fraction=MathF.Min(fraction,EnbCameraBoxCollision.Sweep(extent,movement,Local(box.Center-origin),box.Extents,Local(box.X),Local(box.Y),Local(box.Z)));continue;}
                var packet=(EnbCameraCollisionMesh)leaf;
                if(packet.FirstHitGroup is {} group && hitGroups.Contains(group))continue;
                // Retained runtime mask, including configured postprocessing.
                if((packet.Geometry.Mask&0x10)==0)continue;
                IReadOnlyList<Vector3> positions=packet.Geometry.Positions;
                var indices=packet.Geometry.Indices;
                bool hit=false;
                for(int i=0;i+2<indices.Count;i+=3)
                {
                    float candidate=Sweep(Local(Vector3.Transform(positions[(int)indices[i]],packet.World)-origin),
                        Local(Vector3.Transform(positions[(int)indices[i+1]],packet.World)-origin),
                        Local(Vector3.Transform(positions[(int)indices[i+2]],packet.World)-origin),extent,movement);
                    hit|=candidate<1&&candidate<=fraction;
                    fraction=MathF.Min(fraction,candidate);
                }
                if(hit&&packet.FirstHitGroup is {} hitGroup)hitGroups.Add(hitGroup);
            }
        }
        if(terrain is not null)
        {
            float cell=terrain.HorizontalScale/terrain.Width;
            int x0=(int)MathF.Floor((MathF.Min(origin.X,end.X)-sphere)/cell+terrain.Width/2f);
            int x1=(int)MathF.Ceiling((MathF.Max(origin.X,end.X)+sphere)/cell+terrain.Width/2f);
            int y0=(int)MathF.Floor((MathF.Min(origin.Y,end.Y)-sphere)/cell+terrain.Width/2f);
            int y1=(int)MathF.Ceiling((MathF.Max(origin.Y,end.Y)+sphere)/cell+terrain.Width/2f);
            for(int y=y0;y<y1;y++)for(int x=x0;x<x1;x++)
            {
                var a=Local(terrain.Position(x,y)-origin);var b=Local(terrain.Position(x+1,y)-origin);
                var c=Local(terrain.Position(x+1,y+1)-origin);var d=Local(terrain.Position(x,y+1)-origin);
                fraction=MathF.Min(fraction,MathF.Min(Sweep(a,b,c,extent,movement),Sweep(a,c,d,extent,movement)));
            }
        }
        if(fraction>=1)return camera;
        fraction=MathF.Max(fraction,MathF.Min(1,(shipRadius+near)/distance));
        return camera with {Position=ship+delta*fraction};
    }

    private static bool IntersectsCandidate(EnbCameraCollider collider,Vector3 center,Vector3 extent)
    {
        foreach(var box in collider.Boxes)
            if(EnbCameraBoxCollision.Overlap(extent,box.Center-center,box.Extents,box.X,box.Y,box.Z))return true;
        foreach(var mesh in collider.Meshes)
        {
            if((mesh.Geometry.Mask&16)==0)continue;
            if(!UseLinearOverlapQueries && mesh.Geometry.QueryTree is {} tree)
            {
                if(tree.Intersects(mesh.World,center,extent))return true;
                continue;
            }
            var vertices=mesh.Geometry.Positions;var indices=mesh.Geometry.Indices;
            for(int i=0;i+2<indices.Count;i+=3)
                if(StaticOverlap(Vector3.Transform(vertices[(int)indices[i]],mesh.World)-center,
                    Vector3.Transform(vertices[(int)indices[i+1]],mesh.World)-center,
                    Vector3.Transform(vertices[(int)indices[i+2]],mesh.World)-center,extent))return true;
        }
        return false;
    }

    // 8D0740/8D1BA0 uses the same inward contact tolerance as the moving query.
    internal static bool StaticOverlap(Vector3 a,Vector3 b,Vector3 c,Vector3 extent)
    {
        var ab=b-a;var ac=c-a;
        Span<Vector3> axes=stackalloc Vector3[13];
        axes[0]=Vector3.Cross(ab,ac);axes[1]=Vector3.UnitX;axes[2]=Vector3.UnitY;axes[3]=Vector3.UnitZ;
        int n=4;
        for(int e=0;e<3;e++)
        {var edge=e==0?ab:e==1?ac:ac-ab;axes[n++]=Vector3.Cross(Vector3.UnitX,edge);axes[n++]=Vector3.Cross(Vector3.UnitY,edge);axes[n++]=Vector3.Cross(Vector3.UnitZ,edge);}
        for(int i=0;i<axes.Length;i++)
        {
            var axis=axes[i];float square=Dot(axis,axis);
            if(i>=4&&square<=BitConverter.Int32BitsToSingle(0x322bcc76))continue;
            float origin=Dot(a,axis);if(origin<0){axis=-axis;origin=-origin;}
            float projection=i==0?origin:origin+MathF.Min(0,MathF.Min(Dot(ab,axis),Dot(ac,axis)));
            float gap=projection-Dot(Vector3.Abs(axis),extent);
            float tolerance=gap<=0?MathF.Sqrt(square)*BitConverter.Int32BitsToSingle(0x3a83126f):0;
            if(gap>-tolerance)return false;
        }
        return true;
        static float Dot(Vector3 x,Vector3 y)=>(x.X*y.X+x.Y*y.Y)+x.Z*y.Z;
    }

    private static float Sweep(Vector3 a,Vector3 b,Vector3 c,Vector3 extent,Vector3 movement)
    {
        // 8CCBE0/8CF690: oriented approach planes, including three movement
        // cross axes. A shallow contact moving away is not initial penetration.
        // This is not the usual entry/exit-interval SAT sweep.
        var ab=b-a;var ac=c-a;
        Span<Vector3> axes=stackalloc Vector3[16];
        axes[0]=Vector3.Cross(ab,ac);axes[1]=Vector3.UnitX;axes[2]=Vector3.UnitY;axes[3]=Vector3.UnitZ;
        int n=4;
        for(int e=0;e<3;e++)
        {var edge=e==0?ab:e==1?ac:ac-ab;axes[n++]=Vector3.Cross(Vector3.UnitX,edge);axes[n++]=Vector3.Cross(Vector3.UnitY,edge);axes[n++]=Vector3.Cross(Vector3.UnitZ,edge);}
        axes[13]=Vector3.Cross(movement,Vector3.UnitX);axes[14]=Vector3.Cross(movement,Vector3.UnitY);axes[15]=Vector3.Cross(movement,Vector3.UnitZ);
        float enter=-1;
        for(int i=0;i<axes.Length;i++)
        {
            var axis=axes[i];float square=Dot(axis,axis);
            if(i>=4 && square<=BitConverter.Int32BitsToSingle(0x322bcc76))continue;
            float origin=Dot(a,axis);
            if(origin<0){axis=-axis;origin=-origin;}
            float projection=i==0?origin:origin+MathF.Min(0,MathF.Min(Dot(ab,axis),Dot(ac,axis)));
            float radius=Dot(Vector3.Abs(axis),extent);
            float gap=projection-radius;
            float tolerance=gap<=0?MathF.Sqrt(square)*BitConverter.Int32BitsToSingle(0x3a83126f):0;
            if(gap<=-tolerance)continue;
            // Preserve the stored add/subtract in 8CF690 rather than cancelling
            // radius algebraically; small movement can round away here.
            float speed=(Dot(movement,axis)+radius)-radius;
            if(speed<=0)return 1;
            float fraction=gap/speed;
            if(fraction>=1)return 1;
            enter=MathF.Max(enter,fraction);
        }
        return MathF.Max(0,enter);

        static float Dot(Vector3 x,Vector3 y)=>(x.X*y.X+x.Y*y.Y)+x.Z*y.Z;
    }
}
