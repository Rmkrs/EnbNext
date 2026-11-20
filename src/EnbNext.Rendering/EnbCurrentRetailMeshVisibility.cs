using System.Numerics;
using System.Runtime.CompilerServices;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>0093F390 non-skin mesh admission, using the stored camera frustum
/// (008DD7A0), world AA box (008C2F30), and outside test (008E2000).
/// Camera storage is reused; authored boxes follow their mesh's lifetime.</summary>
public sealed class EnbCurrentRetailMeshVisibility
{
    internal sealed class LocalBox
    {
        public Vector3 Center,Extent;
        private Vector3 _min,_max;
        private bool _ready;
        public void Update(EnbW3dRenderMesh mesh,float scale)
        {
            var min=mesh.BoundingBoxMin;var max=mesh.BoundingBoxMax;
            if(mesh.CurrentRetailLoadedGeometry is {} geometry&&mesh.CurrentRetailLoadedGeometryScale==scale)
            {min=geometry.Minimum;max=geometry.Maximum;}
            else
            {
                using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
                for(int i=0;i<3;i++){min[i]=(float)((R)min[i]*scale);max[i]=(float)((R)max[i]*scale);}
            }
            if(_ready&&_min==min&&_max==max)return;
            _min=min;_max=max;_ready=true;
            (Center,Extent)=EnbCurrentRetailRenderBoundsStorage.MeshLocalBox(min,max);
        }
    }
    private readonly List<Frustum> _cameras=[];
    private readonly ConditionalWeakTable<object,WorldBox> _worldBoxes=new();
    private sealed class WorldBox
    {
        public Vector3 Center,Extent;
        private Matrix4x4 _world;
        private Vector3 _center,_extent;
        private bool _ready,_sphere;
        public void Update(Matrix4x4 world,Vector3 center,Vector3 extent,bool sphere=false)
        {
            if(_ready&&_world==world&&_center==center&&_extent==extent&&_sphere==sphere)return;
            _world=world;_center=center;_extent=extent;_sphere=sphere;_ready=true;
            if(sphere){Center=TransformSphereCenter(world,center);Extent=extent;}
            else (Center,Extent)=TransformBox(world,center,extent);
        }
    }
    public bool Admits(EnbW3dDrawTask packet,EnbResolvedRenderCamera camera)
    {
        // The native model flag, not presence of bone arrays, controls bypass.
        uint flags=packet.Mesh.CurrentRetailLoadedGeometry?.Flags??
            (EnbCurrentRetailMeshGeometryLoader.TranslateInitialFlags(packet.Mesh.GeometryVersion,packet.Mesh.Attributes,1)|
                (packet.Mesh.BoneIndices.Count==0?0u:0x400u));
        if((flags&0x400)!=0)return true;
        Frustum? frustum=null;
        for(int i=_cameras.Count-1;i>=0;i--)if(_cameras[i].Camera==camera){frustum=_cameras[i];break;}
        if(frustum is null)
        {
            if(_cameras.Count<8){frustum=new();_cameras.Add(frustum);}
            else {frustum=_cameras[0];_cameras.RemoveAt(0);_cameras.Add(frustum);}
            frustum.Update(camera);
        }
        var worldBox=_worldBoxes.GetValue(packet.PreparedIdentity,static _=>new());
        var world=EnbRetailProjectedDecal.MeshWorld(packet);
        //00940EB0 is MeshClass's override of cached bounds. Internal
        //ALIGNED/ORIENTED flags use the world sphere's enclosing box;
        //the camera-relative draw basis is not known at this stage.
        if((flags&0xa00)!=0)
        {
            var mesh=packet.Mesh;float scale=packet.CurrentRetailMeshModelScale;
            Vector3 center;float radius;
            if(mesh.CurrentRetailLoadedGeometry is {} geometry&&mesh.CurrentRetailLoadedGeometryScale==scale)
            {center=geometry.Center;radius=geometry.Radius;}
            else
            {
                using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
                center=new((float)((R)mesh.BoundingSphereCenter.X*scale),
                    (float)((R)mesh.BoundingSphereCenter.Y*scale),(float)((R)mesh.BoundingSphereCenter.Z*scale));
                radius=(float)((R)mesh.BoundingRadius*scale);
            }
            worldBox.Update(world,center,new Vector3(radius),true);
        }
        else
        {
            var box=OwnerCache.Get(ref packet.Mesh.DerivedVisibilityLocalBox,packet.Mesh,static _=>new LocalBox());
            box.Update(packet.Mesh,packet.CurrentRetailMeshModelScale);
            worldBox.Update(world,box.Center,box.Extent);
        }
        return !OutsidePlanes(frustum.Planes,worldBox.Center,worldBox.Extent);
    }
    //00940ECB..00940F2A: preserve the native sphere-center operation order.
    public static Vector3 TransformSphereCenter(Matrix4x4 m,Vector3 c,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision,true);
        return new((float)((((R)m.M31*c.Z+(R)m.M21*c.Y)+(R)m.M11*c.X)+m.M41),
            (float)((((R)m.M12*c.X+(R)m.M32*c.Z)+(R)m.M22*c.Y)+m.M42),
            (float)((((R)m.M13*c.X+(R)m.M33*c.Z)+(R)m.M23*c.Y)+m.M43));
    }
    public static bool OutsidePlanes(ReadOnlySpan<Vector4> planes,Vector3 center,Vector3 extent,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision,true);
        foreach(var plane in planes)
        {
            float ex=BitConverter.SingleToInt32Bits(plane.X)<0?-extent.X:extent.X;
            float ey=BitConverter.SingleToInt32Bits(plane.Y)<0?-extent.Y:extent.Y;
            float ez=BitConverter.SingleToInt32Bits(plane.Z)<0?-extent.Z:extent.Z;
            //008E20A1..20B6: Z product plus X product, then Y, then D.
            R z=((R)(-ez)+center.Z)*plane.Z,x=((R)(-ex)+center.X)*plane.X;
            R y=((R)(-ey)+center.Y)*plane.Y;
            if(((z+x)+y)-plane.W>BitConverter.UInt32BitsToSingle(0x358637bd))return true;
        }
        return false;
    }
    private sealed class Frustum
    {
        public readonly Vector4[] Planes=new Vector4[6];
        public EnbResolvedRenderCamera Camera;
        public void Update(EnbResolvedRenderCamera camera)
        {
            Camera=camera;
            var m=EnbBridgeCameraComposition.RetailTransform(camera);
            ReadOnlySpan<float> rows=[m.M11,m.M21,m.M31,m.M41,m.M12,m.M22,m.M32,m.M42,m.M13,m.M23,m.M33,m.M43];
            var b=camera.Projection.StoredProjectionBounds??throw new InvalidOperationException("Mesh admission requires stored retail projection bounds.");
            EnbRetailFrustumPlanes.Build(rows,b,-camera.Projection.ClipRange.NearPlane,-camera.Projection.ClipRange.FarPlane,Planes);
        }
    }
    public static (Vector3 Center,Vector3 Extent) TransformBox(Matrix4x4 world,Vector3 center,Vector3 extent)
    {
            using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
            ReadOnlySpan<float> matrix=[world.M11,world.M21,world.M31,world.M41,world.M12,world.M22,world.M32,world.M42,world.M13,world.M23,world.M33,world.M43];
            Span<float> output=stackalloc float[6];
            for(int row=0;row<3;row++)
            {
                float c=matrix[row*4+3],e=0;
                for(int col=0;col<3;col++)
                {
                    c=(float)((R)center[col]*matrix[row*4+col]+c);
                    float size=(float)((R)extent[col]*matrix[row*4+col]);
                    e=(float)((R)BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(size)&0x7fffffff)+e);
                }
                output[row]=c;output[row+3]=e;
            }
            return (new(output[0],output[1],output[2]),new(output[3],output[4],output[5]));
    }
}
