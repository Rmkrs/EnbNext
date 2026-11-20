using System.Numerics;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>Retail camera projection matrix: the 00944EB0 projection rows and,
/// for the backend, the 00945A60 depth terms. Operation order and x87 PC24
/// rounding follow the retail code. The result is transposed for
/// System.Numerics row vectors.</summary>
public static class EnbRetailProjectionMatrix
{
    public static Matrix4x4 ComputeBackend(Vector4 storedBounds,EnbRenderClipRange clip,bool orthographic)
        => Compute(storedBounds,clip,orthographic,true);

    /// <param name="storedBounds">Left, bottom, right and top at unit depth.</param>
    public static Matrix4x4 Compute(Vector4 storedBounds,EnbRenderClipRange clip,bool orthographic,bool backendDepth)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        Span<float> m=stackalloc float[16];
        m.Clear();m[0]=m[5]=m[10]=m[15]=1;
        float left=storedBounds.X,bottom=storedBounds.Y,right=storedBounds.Z,top=storedBounds.W;
        float near=clip.NearPlane,far=clip.FarPlane;
        if(!orthographic)
        {
            R topNear=(R)top*near;
            R bottomNear=(R)bottom*near;
            float rightNear=(float)((R)right*near);
            float leftNear=(float)((R)left*near);
            R twiceNear=(R)near+near;
            R width=(R)rightNear-leftNear;
            m[0]=(float)(twiceNear/width);
            m[2]=(float)(((R)leftNear+rightNear)/width);
            R height=topNear-bottomNear;float storedHeight=(float)height;
            m[5]=(float)(twiceNear/height);
            m[6]=(float)((bottomNear+topNear)/storedHeight);
            R depth=(R)far-near;
            m[10]=(float)(-(((R)near+far)/depth));
            R product=(R)near*far;
            m[11]=(float)(-((product+product)/depth));
            m[14]=-1;m[15]=0;
        }
        else
        {
            R width=(R)right-left;
            m[0]=(float)((R)2f/width);
            m[3]=(float)(-(((R)left+right)/width));
            R height=(R)top-bottom;
            m[5]=(float)((R)2f/height);
            m[7]=(float)(-(((R)bottom+top)/height));
            R depth=(R)far-near;
            m[10]=(float)((R)(-2f)/depth);
            m[11]=(float)(-(((R)near+far)/depth));
        }
        if(backendDepth)
        {
            R inverse=(R)1.0/((R)far-near);
            if(!orthographic)
            {
                m[10]=(float)(-((R)far*inverse));
                m[11]=(float)(-(((R)far*near)*inverse));
            }
            else
            {
                m[10]=(float)(-inverse);
                m[11]=(float)(-(inverse*near));
            }
        }
        return new(m[0],m[4],m[8],m[12],m[1],m[5],m[9],m[13],m[2],m[6],m[10],m[14],m[3],m[7],m[11],m[15]);
    }
}
