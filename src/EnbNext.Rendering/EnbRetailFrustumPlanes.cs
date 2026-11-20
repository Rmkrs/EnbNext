using System.Numerics;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>008DD7A0 camera frustum planes: eight corners transformed by the
/// camera rows, then six normalized planes (00447C40/004481E0 arithmetic).
/// Operation order and x87 PC24 rounding follow the retail code.</summary>
internal static class EnbRetailFrustumPlanes
{
    /// <param name="rows">Camera transform, three rows of four.</param>
    /// <param name="bounds">Left, bottom, right and top at unit depth.</param>
    /// <param name="nearValue">Negated near plane.</param>
    /// <param name="farValue">Negated far plane.</param>
    public static void Build(ReadOnlySpan<float> rows,Vector4 bounds,float nearValue,float farValue,Span<Vector4> planes)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        R near=nearValue;float far=farValue;
        R zero=0f;
        if(near>zero&&(R)far>zero){near=-near;far=(float)(-(R)far);}
        Span<float> corners=stackalloc float[24];
        float lowerX=Loaded(bounds.X),lowerY=Loaded(bounds.Y),upperX=Loaded(bounds.Z),upperY=Loaded(bounds.W);
        ReadOnlySpan<float> xs=[lowerX,upperX,lowerX,upperX],ys=[upperY,upperY,lowerY,lowerY];
        for(int corner=0;corner<4;corner++)
        {
            ReadOnlySpan<float> point=[xs[corner],ys[corner],1f];
            for(int axis=0;axis<3;axis++)
            {
                corners[corner*3+axis]=(float)(near*point[axis]);
                corners[12+corner*3+axis]=(float)((R)far*point[axis]);
            }
        }
        for(int corner=0;corner<8;corner++)
        {
            int c=corner*3;
            R x=corners[c],y=corners[c+1],z=corners[c+2];
            corners[c]=(float)((y*rows[1]+x*rows[0])+z*rows[2]+rows[3]);
            corners[c+1]=(float)((x*rows[4]+z*rows[6])+y*rows[5]+rows[7]);
            corners[c+2]=(float)((x*rows[8]+z*rows[10])+y*rows[9]+rows[11]);
        }
        ReadOnlySpan<int> origins=[0,0,0,2,1,4],aCorners=[1,4,2,3,5,6],bCorners=[3,5,6,7,7,7];
        Span<float> output=stackalloc float[4];
        for(int plane=0;plane<6;plane++)
        {
            int o=origins[plane]*3,a=aCorners[plane]*3,b=bCorners[plane]*3;
            R ax=(R)corners[a]-corners[o],ay=(R)corners[a+1]-corners[o+1],az=(R)corners[a+2]-corners[o+2];
            R bx=(float)((R)corners[b]-corners[o]),by=(float)((R)corners[b+1]-corners[o+1]),bz=(R)corners[b+2]-corners[o+2];
            if(plane==5){ax=(float)ax;ay=(float)ay;az=(float)az;bz=(float)bz;}
            R x=(float)(by*az-bz*ay),y=(float)(bz*ax-az*bx),z=ay*bx-by*ax;
            if(plane==5)z=(float)z;
            output[0]=(float)x;output[1]=(float)y;output[2]=(float)z;
            if(EqualOrUnordered(x,zero)&&EqualOrUnordered(y,zero)&&EqualOrUnordered(z,zero)){planes[plane]=new(0,0,1,0);continue;}
            R square=plane==1?(z*z+y*y)+x*x:plane==5?(x*x+y*y)+z*z:(x*x+z*z)+y*y;
            if(!EqualOrUnordered(square,zero))
            {
                R scale=EnbRetailLineCoalescing.ReciprocalSquareRoot((float)square);
                for(int axis=0;axis<3;axis++)output[axis]=(float)(scale*output[axis]);
            }
            R px=(R)output[0]*corners[o],py=(R)output[1]*corners[o+1],pz=(R)output[2]*corners[o+2];
            R distance=plane==1?(pz+py)+px:plane==2?(py+px)+pz:plane==3?(py+pz)+px:plane==5?(px+pz)+py:(pz+px)+py;
            planes[plane]=new(output[0],output[1],output[2],(float)distance);
        }
    }
    private static bool EqualOrUnordered(R value,R zero)=>!(value>zero)&&!(value<zero);
    // FLD of a stored float quiets a signaling NaN before the value is used.
    private static float Loaded(float value)=>BitConverter.UInt32BitsToSingle(EnbRetailX87Load.LoadedFloatWord(BitConverter.SingleToUInt32Bits(value)));
}
