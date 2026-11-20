using System.Numerics;
using System.Runtime.InteropServices;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>Resolved camera binding for the world provider's 00944890 call:
/// the camera's rigid inverse and projection rows, and the point test.
/// Operation order and x87 PC24 rounding follow the retail code.</summary>
internal sealed class EnbRetailDazzleSampleProjection
{
    private EnbResolvedRenderCamera camera;
    private bool initialized;
    private readonly float[] view=new float[12],projection=new float[16];
    private float nearPlane;

    public EnbRetailDazzleSampleProjection(EnbResolvedRenderCamera camera)=>Bind(camera);
    internal void Bind(EnbResolvedRenderCamera camera)
    {
        var previous=this.camera;
        if(initialized&&MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref camera,1)).SequenceEqual(
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref previous,1))))return;
        this.camera=camera;initialized=true;
        // Rigid inverse of the camera rows (right, up, back; position).
        Vector3 right=camera.Basis.Right,up=camera.Basis.Up,back=-camera.Basis.Forward;
        view[0]=right.X;view[1]=right.Y;view[2]=right.Z;
        view[4]=up.X;view[5]=up.Y;view[6]=up.Z;
        view[8]=back.X;view[9]=back.Y;view[10]=back.Z;
        float tx=Loaded(camera.Position.X),ty=Loaded(camera.Position.Y),tz=Loaded(camera.Position.Z);
        using(new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24))
        {
            R z=((R)view[10]*tz+(R)view[8]*tx)+((R)view[9]*ty);
            R y=((R)view[6]*tz+(R)view[4]*tx)+((R)view[5]*ty);
            R x=((R)view[2]*tz+(R)view[1]*ty)+((R)tx*view[0]);
            view[3]=Negated((float)x);view[7]=Negated((float)y);view[11]=Negated((float)z);
        }
        var matrix=camera.Projection.Matrix;
        if(camera.Projection.StoredProjectionBounds is {} bounds)
            matrix=EnbRetailProjectionMatrix.Compute(bounds,camera.Projection.ClipRange,matrix.M44==1,false);
        else
        {
            matrix.M13=2*matrix.M13-matrix.M14;
            matrix.M23=2*matrix.M23-matrix.M24;
            matrix.M33=2*matrix.M33-matrix.M34;
            matrix.M43=2*matrix.M43-matrix.M44;
        }
        ReadOnlySpan<float> rows=[matrix.M11,matrix.M21,matrix.M31,matrix.M41,
            matrix.M12,matrix.M22,matrix.M32,matrix.M42,
            matrix.M13,matrix.M23,matrix.M33,matrix.M43,
            matrix.M14,matrix.M24,matrix.M34,matrix.M44];
        rows.CopyTo(projection);
        nearPlane=camera.Projection.ClipRange.NearPlane;
    }

    /// <summary>True when the point projects inside the unit clip square
    /// in front of the near plane and not beyond the far plane.</summary>
    public bool Contains(Vector3 point)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        ReadOnlySpan<float> input=[point.X,point.Y,point.Z];
        Span<float> v=[(float)Affine(view,0,input,true),(float)Affine(view,4,input,false),(float)Affine(view,8,input,false)];
        R near=-(R)nearPlane,depth=v[2];
        if(near<depth||Unordered(near,depth))return false;
        R x=Affine(projection,0,v,true);
        float y=(float)Affine(projection,4,v,false),z=(float)Affine(projection,8,v,false);
        float inverse=(float)((R)1f/Affine(projection,12,v,false));
        x*=inverse;
        R dividedY=(float)((R)y*inverse);
        R dividedZ=(R)z*inverse;
        R one=1f,minimum=-1f;
        if(dividedZ>one&&!Unordered(dividedZ,one))return false;
        if(x<minimum||Unordered(x,minimum)||x>one&&!Unordered(x,one))return false;
        return !(dividedY<minimum||Unordered(dividedY,minimum)||dividedY>one&&!Unordered(dividedY,one));
    }

    private static R Affine(ReadOnlySpan<float> m,int row,ReadOnlySpan<float> input,bool first)
    {
        R y=(R)m[row+1]*input[1];
        if(first)return (y+(R)m[row+2]*input[2])+(R)input[0]*m[row]+m[row+3];
        return (y+(R)m[row]*input[0])+(R)m[row+2]*input[2]+m[row+3];
    }
    private static bool Unordered(R first,R second)=>double.IsNaN((double)first)||double.IsNaN((double)second);
    // FLD of a stored float quiets a signaling NaN before the value is used.
    private static float Loaded(float value)=>BitConverter.UInt32BitsToSingle(EnbRetailX87Load.LoadedFloatWord(BitConverter.SingleToUInt32Bits(value)));
    private static float Negated(float value)=>BitConverter.UInt32BitsToSingle(EnbRetailX87Load.LoadedFloatWord(BitConverter.SingleToUInt32Bits(value))^0x80000000);
}
