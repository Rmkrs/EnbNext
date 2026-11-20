using System.Numerics;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>Inherited cached world bounds 009421B0 and the mesh and box
/// local bound virtuals, at retail PC24 with the retail operation order.</summary>
public static class EnbCurrentRetailRenderBoundsStorage
{
    /// <summary>Finite0092A840 MeshModel local box. The Y/Z sums and
    /// differences spill before multiplication; X remains in a register.</summary>
    public static (Vector3 Center,Vector3 Extent) MeshLocalBox(Vector3 minimum,Vector3 maximum,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
        float cy=(float)((R)maximum.Y+minimum.Y),cz=(float)((R)maximum.Z+minimum.Z);
        Vector3 center=new((float)(((R)minimum.X+maximum.X)*.5f),(float)((R)cy*.5f),(float)((R)cz*.5f));
        float ey=(float)((R)maximum.Y-minimum.Y),ez=(float)((R)maximum.Z-minimum.Z);
        Vector3 extent=new((float)(((R)maximum.X-minimum.X)*.5f),(float)((R)ey*.5f),(float)((R)ez*.5f));
        return (center,extent);
    }
    /// <summary>Finite local box virtuals999910/99A760 and999980/99A7D0.
    /// Squared extents spill before FSQRT; the resulting radius spills again.</summary>
    public static EnbCurrentRetailCollectionBounds BoxLocalBounds(Vector3 center,Vector3 extent,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
        float squared=(float)((((R)extent.X*extent.X)+((R)extent.Y*extent.Y))+((R)extent.Z*extent.Z));
        float radius=(float)R.Sqrt(squared);
        return new(new(center,radius),center,extent);
    }
    /// <summary>Finite semantic adapter to the same raw world-bounds effect.
    /// Matrix4x4 is the renderer's row-vector representation of native rows.</summary>
    public static EnbCurrentRetailCollectionBounds Calculate(Matrix4x4 matrix,Vector4 localSphere,
        Vector3 localBoxCenter,Vector3 localBoxExtent,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24,bool nativeNaNs=false)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision,nativeNaNs);
        R x=(((R)matrix.M31*localSphere.Z+(R)matrix.M21*localSphere.Y)+
            (R)matrix.M11*localSphere.X)+matrix.M41;
        float y=(float)((((R)matrix.M12*localSphere.X+(R)matrix.M32*localSphere.Z)+
            (R)matrix.M22*localSphere.Y)+matrix.M42);
        float z=(float)((((R)matrix.M13*localSphere.X+(R)matrix.M33*localSphere.Z)+
            (R)matrix.M23*localSphere.Y)+matrix.M43);
        Vector3 center=matrix.Translation,extent=Vector3.Zero;
        for(int column=0;column<3;column++)
        {
            Vector3 basis=column==0?new(matrix.M11,matrix.M12,matrix.M13):
                column==1?new(matrix.M21,matrix.M22,matrix.M23):new(matrix.M31,matrix.M32,matrix.M33);
            for(int row=0;row<3;row++)
            {
                center[row]=(float)((R)localBoxCenter[column]*basis[row]+center[row]);
                float product=(float)((R)localBoxExtent[column]*basis[row]);
                float positive=BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(product)&0x7fffffff);
                extent[row]=(float)((R)positive+extent[row]);
            }
        }
        return new(new((float)x,y,z,localSphere.W),center,extent);
    }
}
