using System.Numerics;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>LogoWidget's camera-origin mesh query (9671B0 ->9422E0 ->940530).
/// Retains model collision geometry independently of GPU deformation.</summary>
public readonly record struct EnbRetailMeshPick(EnbRetailDazzleMeshRay Mesh,Matrix4x4 World,EnbResolvedRenderCamera Camera)
{
    //9671B0 visits children first, then9422E0 on their HLOD parent.
    //98E680's last-level child D0 calls do not repeat each leaf's sphere test.
    //Only callers retaining that parent's D0 child set may supply this gate.
    public EnbW3dWorldBounds? ParentSphere {get;init;}
    public EnbW3dWorldBounds? AncestorSphere {get;init;}
    public IReadOnlyList<EnbW3dWorldBounds>? AdditionalAncestorSpheres {get;init;}
    public bool Contains(double x,double y)
    {
        if(!TryGetRay(x,y,out var direction,out var end))return false;
        if(!IntersectsSphere(Mesh.ModelSphereCenter,Mesh.ModelSphereRadius,World,direction) &&
            !(ParentSphere is {} parent && IntersectsSphere(parent.Center,parent.Radius,Matrix4x4.Identity,direction)) &&
            !(AncestorSphere is {} ancestor && IntersectsSphere(ancestor.Center,ancestor.Radius,Matrix4x4.Identity,direction)) &&
            !IntersectsAdditionalAncestors(direction))return false;
        return Mesh.CastSegment(World,Camera.Position,end).Fraction<1;
    }
    private bool IntersectsAdditionalAncestors(Vector3 direction)
    {
        if(AdditionalAncestorSpheres is {} spheres)
            foreach(var sphere in spheres)
                if(IntersectsSphere(sphere.Center,sphere.Radius,Matrix4x4.Identity,direction))return true;
        return false;
    }
    private bool IntersectsSphere(Vector3 center,float radius,Matrix4x4 world,Vector3 direction)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        //9426A0's line/sphere rejection; it does not clip at the near plane.
        R dx=(float)((R)center.Z*world.M31+(R)center.Y*world.M21+(R)center.X*world.M11+world.M41)-(R)Camera.Position.X;
        R dy=(float)((R)center.X*world.M12+(R)center.Z*world.M32+(R)center.Y*world.M22+world.M42)-(R)Camera.Position.Y;
        R dz=(float)((R)center.X*world.M13+(R)center.Z*world.M33+(R)center.Y*world.M23+world.M43)-(R)Camera.Position.Z;
        R along=(dz*direction.Z+dy*direction.Y)+dx*direction.X;
        R discriminant=(R)radius*radius-(((dz*dz+dy*dy)+dx*dx)-along*along);
        return !double.IsNaN((double)discriminant)&&discriminant>=0;
    }

    internal bool TryGetRay(double x,double y,out Vector3 direction,out Vector3 end)
    {
        direction=end=default;
        if(!double.IsFinite(x)||!double.IsFinite(y)||x<0||x>1||y<0||y>1||
            Camera.Projection.StoredProjectionBounds is not {} bounds)return false;
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        //9672DD..9673D8. Station cameras retain zero view-plane offsets.
        //Use the stored bounds and native reciprocal square root, not an
        //inverse GPU matrix (which also introduces the near-plane origin).
        R horizontal=((R)0-(float)x+.5)*((R)bounds.Z-bounds.X)*-1;
        float vertical=(float)(((R)(float)y-.5)*((R)bounds.W-bounds.Y)*-1);
        var basis=Camera.Basis;
        direction=new((float)((R)basis.Up.X*vertical+(R)basis.Right.X*horizontal+basis.Forward.X),
            (float)((R)basis.Up.Y*vertical+(R)basis.Right.Y*horizontal+basis.Forward.Y),
            (float)((R)basis.Up.Z*vertical+(R)basis.Right.Z*horizontal+basis.Forward.Z));
        float square=(float)(((R)direction.X*direction.X+(R)direction.Y*direction.Y)+(R)direction.Z*direction.Z);
        if(square!=0)
        {
            R inverse=EnbRetailLineCoalescing.ReciprocalSquareRoot(square);
            direction=new((float)(inverse*direction.X),(float)(inverse*direction.Y),(float)(inverse*direction.Z));
        }
        R length=Camera.Projection.ClipRange.FarPlane;
        end=new Vector3((float)((R)(float)(length*direction.X)+Camera.Position.X),
            (float)((R)(float)(length*direction.Y)+Camera.Position.Y),(float)(length*direction.Z+Camera.Position.Z));
        return true;
    }
}
