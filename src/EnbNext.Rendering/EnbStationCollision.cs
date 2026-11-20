using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Continuous box/triangle sweeps against authored station collision meshes.</summary>
public sealed class EnbStationCollision
{
    private readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, uint Mask,uint Surface);
    public uint FloorSurface { get; private set; }
    private uint _sweepSurface;
    private readonly List<Triangle> _triangles = [];
    private readonly List<Triangle> _actorTriangles = [];
    private readonly Vector3 _center, _extent;
    private readonly float _headHeight;
    // Retail 0063F0D0 casts a 0.1 box using collision mask 2 (W3D physical).
    public bool OccludesName(Vector3 camera, Vector3 anchor)
        => Sweep(camera,new(.1f),anchor-camera,0x10,out _,out _);
    public EnbStationCollision(EnbCurrentRetailStationInterior room, EnbRetailAssetBundle assets, float headHeight)
    {
        _headHeight = headHeight;
        if (!assets.RetailMixResources!.TryReadResource("avcollision.w3d", out var bytes, out _) ||
            !EnbW3dModel.TryDecode(bytes, out var model)) throw new InvalidDataException("Avatar collision bounds unavailable.");
        var bounds = model.Meshes.Single();
        _center = (bounds.BoundingBoxMin + bounds.BoundingBoxMax) * .5f;
        _extent = (bounds.BoundingBoxMax - bounds.BoundingBoxMin) * .5f;
        // 00787F40 raises the collision box center before horizontal sweeps.
        _center.Z += .1f;
        Add(room.Resource,Matrix4x4.Identity);
        foreach(var door in room.Doorways)Add(door.Resource,door.Transform);
        if (_triangles.Count == 0) throw new InvalidDataException("Station collision geometry unavailable.");
    }
    public void Add(EnbW3dRenderResource resource,Matrix4x4 transform)
        => Add(resource,transform,_triangles);
    /// <summary>
    /// Every collision triangle of a room, its doors and the given fixtures (as movement collides with them), for the
    /// Next station walking grid and map; Physical marks the ones avatar movement sweeps against (mask 0x10).
    /// Nothing in movement uses this.
    /// </summary>
    public static List<(Vector3 A,Vector3 B,Vector3 C,bool Physical)> Surfaces(EnbCurrentRetailStationInterior room,
        IEnumerable<(EnbW3dRenderResource Resource,Matrix4x4 World)> fixtures)
    {
        var all=new List<Triangle>();
        Add(room.Resource,Matrix4x4.Identity,all);
        foreach(var door in room.Doorways)Add(door.Resource,door.Transform,all);
        foreach(var (resource,world) in fixtures)Add(resource,world,all);
        return all.ConvertAll(t=>(t.A,t.B,t.C,(t.Mask&0x10)!=0));
    }
    // Avatar construction 0077DA20 attaches avcollision to skel_reference.
    // Scene collision therefore follows the current actor, not its spawn slot.
    public void ReplaceActors(EnbW3dRenderResource resource,IEnumerable<Matrix4x4> transforms)
    {
        _actorTriangles.Clear();
        foreach(var transform in transforms)Add(resource,transform,_actorTriangles);
    }
    private static void Add(EnbW3dRenderResource resource,Matrix4x4 transform,List<Triangle> triangles)
    {
        if(resource.HierarchicalLods.Count==0)
        {
            foreach(var mesh in resource.Meshes)AddMesh(mesh,transform,triangles);
            return;
        }
        var hierarchy = resource.Hierarchies.Single();
        if (!EnbW3dHierarchyPose.TryEvaluate(hierarchy, transform, out var pose, out var error)) throw new InvalidDataException(error);
        foreach (var member in resource.HierarchicalLods.Single().Levels.Single().Members)
        {
            var mesh = resource.Meshes.SingleOrDefault(m => member.Name.Equals(m.ContainerName + "." + m.Name,StringComparison.OrdinalIgnoreCase) || member.Name.Equals(m.Name,StringComparison.OrdinalIgnoreCase));
            if (mesh is null) continue;
            var world = pose[(int)member.ReferenceIndex];
            AddMesh(mesh,world,triangles);
        }
    }
    private static void AddMesh(EnbW3dRenderMesh mesh,Matrix4x4 world,List<Triangle> triangles)
    {
            if((mesh.Attributes&0x30)==0)return;
            for (int i = 0; i < mesh.Indices.Count; i += 3)
                triangles.Add(new(Vector3.Transform(mesh.Positions[(int)mesh.Indices[i]],world),
                    Vector3.Transform(mesh.Positions[(int)mesh.Indices[i+1]],world),
                    Vector3.Transform(mesh.Positions[(int)mesh.Indices[i+2]],world), mesh.Attributes & 0x30,
                    mesh.TriangleMetadata[i/3].Attributes));
    }
    public Vector3 Move(Vector3 position, Vector3 delta)
    {
        var origin = position;
        // 0063BC30 allows ten slide corrections, then restores the prior safe position.
        for (int pass = 0; pass < 10 && delta.LengthSquared() > 1e-10f; pass++)
        {
            if (!Sweep(position + _center, _extent, delta, 0x10, out float fraction, out var normal))
            { position += delta; break; }
            normal.Z = 0;
            if (normal.LengthSquared() < 1e-10f) { position = origin; break; }
            normal = Vector3.Normalize(normal);
            // Reach the contact before sliding the remaining displacement. Projecting
            // the entire step from its old origin can start the next sweep inside a
            // sloping hallway wall after floor-height adjustment.
            position += delta * fraction;
            var remaining=delta*(1-fraction);
            var adjusted=remaining-normal*Math.Min(0,Vector3.Dot(remaining,normal));
            if (pass == 9) { position = origin; break; }
            delta = adjusted;
        }
        // 00640F50: 0.5-extents box cast down 50 from head height +1, mask 4.
        var floorOrigin = position + new Vector3(0,0,_headHeight + 1);
        if (Sweep(floorOrigin,new(.5f),new(0,0,-50),0x20,out float floor,out _))
        { position.Z = floorOrigin.Z - 50 * floor; FloorSurface=_sweepSurface; }
        return position;
    }
    private bool Sweep(Vector3 center, Vector3 extent, Vector3 delta, uint mask, out float fraction, out Vector3 normal)
    {
        fraction = 1; normal = default; bool found = false;
        Span<Vector3> axes=stackalloc Vector3[13];
        foreach (var triangle in _triangles.Concat(_actorTriangles))
        {
            if ((triangle.Mask & mask) == 0) continue;
            var a=triangle.A-center; var b=triangle.B-center; var c=triangle.C-center;
            axes[0]=Vector3.UnitX; axes[1]=Vector3.UnitY; axes[2]=Vector3.UnitZ;
            axes[3]=Vector3.Cross(b-a,c-a);
            int n=4;
            for(int e=0;e<3;e++)
            { var edge=e==0?b-a:e==1?c-b:a-c; axes[n++]=Vector3.Cross(edge,Vector3.UnitX); axes[n++]=Vector3.Cross(edge,Vector3.UnitY); axes[n++]=Vector3.Cross(edge,Vector3.UnitZ); }
            float enter=float.NegativeInfinity,leave=float.PositiveInfinity;
            float penetration=float.PositiveInfinity;Vector3 overlapNormal=default;
            Vector3 contact=default; bool separated=false;
            foreach(var raw in axes)
            {
                if(raw.LengthSquared()<1e-12f)continue;
                var axis=Vector3.Normalize(raw);
                float x=Vector3.Dot(a,axis),y=Vector3.Dot(b,axis),z=Vector3.Dot(c,axis);
                float radius=Vector3.Dot(Vector3.Abs(axis),extent);
                float min=Math.Min(x,Math.Min(y,z))-radius,max=Math.Max(x,Math.Max(y,z))+radius;
                if(min<=0 && max>=0)
                {
                    float depth=Math.Min(-min,max);
                    if(depth<penetration){penetration=depth;overlapNormal=-min<max?-axis:axis;}
                }
                float speed=Vector3.Dot(delta,axis);
                if(Math.Abs(speed)<1e-8f) {if(min>0||max<0){separated=true;break;}continue;}
                float first=min/speed,last=max/speed;
                if(first>last)(first,last)=(last,first);
                if(first>enter){enter=first;contact=speed>0?-axis:axis;}
                leave=Math.Min(leave,last);
                if(enter>leave){separated=true;break;}
            }
            // Floor snapping and roundoff may leave a slight existing overlap.
            // Block motion deeper into it, but allow moving away or along the wall.
            if(!separated && enter<0 && leave>=0 && Vector3.Dot(delta,overlapNormal)<-1e-7f)
            {enter=0;contact=overlapNormal;}
            if(!separated && enter>=-1e-6f && enter<=fraction && leave>=0 && Vector3.Dot(delta,contact)<-1e-7f)
            {fraction=Math.Max(0,enter);normal=contact;found=true;_sweepSurface=triangle.Surface;}
        }
        return found;
    }
}
