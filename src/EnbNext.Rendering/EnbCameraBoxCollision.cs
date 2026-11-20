using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>8C8880/8C89B0 ->8C5460: moving OBB versus retained box.</summary>
public static class EnbCameraBoxCollision
{
    public static bool Overlap(Vector3 extent,Vector3 center,Vector3 otherExtent,Vector3 x,Vector3 y,Vector3 z)
    {
        Span<Vector3> axes=stackalloc Vector3[15];
        axes[0]=Vector3.UnitX;axes[1]=Vector3.UnitY;axes[2]=Vector3.UnitZ;axes[3]=x;axes[4]=y;axes[5]=z;
        int n=6;
        for(int i=0;i<3;i++){var a=axes[i];axes[n++]=Vector3.Cross(a,x);axes[n++]=Vector3.Cross(a,y);axes[n++]=Vector3.Cross(a,z);}
        for(int i=0;i<axes.Length;i++)
        {
            if(i<3&&extent[i]<=0||i is >=3 and <6&&otherExtent[i-3]<=0)continue;
            var axis=axes[i];if(i>=6&&Dot(axis,axis)<=BitConverter.Int32BitsToSingle(0x322bcc76))continue;
            float radius=Dot(Vector3.Abs(axis),extent)+MathF.Abs(Dot(axis,x))*otherExtent.X+MathF.Abs(Dot(axis,y))*otherExtent.Y+MathF.Abs(Dot(axis,z))*otherExtent.Z;
            if(MathF.Abs(Dot(center,axis))>radius)return false;
        }
        return true;
    }
    // Inputs are in the moving box's rigid frame; its center is zero.
    public static float Sweep(Vector3 extent,Vector3 movement,Vector3 center,
        Vector3 otherExtent,Vector3 x,Vector3 y,Vector3 z)
    {
        Span<Vector3> axes=stackalloc Vector3[18];
        axes[0]=Vector3.UnitX;axes[1]=Vector3.UnitY;axes[2]=Vector3.UnitZ;
        axes[3]=x;axes[4]=y;axes[5]=z;
        int n=6;
        for(int i=0;i<3;i++)
        {var a=axes[i];axes[n++]=Vector3.Cross(a,x);axes[n++]=Vector3.Cross(a,y);axes[n++]=Vector3.Cross(a,z);}
        axes[15]=Vector3.Cross(Vector3.UnitX,movement);axes[16]=Vector3.Cross(Vector3.UnitY,movement);axes[17]=Vector3.Cross(Vector3.UnitZ,movement);
        float fraction=0;
        for(int i=0;i<axes.Length;i++)
        {
            var axis=axes[i];if(i>=6&&Dot(axis,axis)<=BitConverter.Int32BitsToSingle(0x322bcc76))continue;
            float radius=Dot(Vector3.Abs(axis),extent)+MathF.Abs(Dot(axis,x))*otherExtent.X+MathF.Abs(Dot(axis,y))*otherExtent.Y+MathF.Abs(Dot(axis,z))*otherExtent.Z;
            float start=Dot(center,axis),end=start-Dot(movement,axis);
            float boundary;
            const float tolerance=.0001f; // AE7840, unlike the triangle tolerance.
            if(start+tolerance>radius){boundary=radius;if(end>boundary)return 1;}
            else if(start-tolerance< -radius){boundary=-radius;if(end<boundary)return 1;}
            else continue;
            float difference=end-start;
            if(MathF.Abs(difference)>0)fraction=MathF.Max(fraction,(boundary-start)/difference);
        }
        return fraction;
    }
    private static float Dot(Vector3 a,Vector3 b)=>(a.X*b.X+a.Y*b.Y)+a.Z*b.Z;
}
