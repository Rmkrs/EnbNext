using System.Numerics;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>0098F1D0 HLOD bounds arithmetic at retail PC24: the box sphere
/// radius (0098D0B0), the selected bounding box (0098CF90) and the selected
/// world sphere (0098D110).</summary>
public static class EnbRetailHlodBounds
{
    public static float BoxSphereRadius(Vector3 extent)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        float squared=(float)((((R)extent.Y*extent.Y)+((R)extent.Z*extent.Z))+((R)extent.X*extent.X));
        return (float)R.Sqrt(squared);
    }
    /// <summary>The selected box child's local box, through the inverse
    /// owner transform composed with the queried pivot matrix.</summary>
    public static (Vector3 Center,Vector3 Extent) TransformSelectedBox(Matrix4x4 ownerMatrix,Matrix4x4 queriedPivotMatrix,Vector3 center,Vector3 extent)
    {
        Span<float> owner=stackalloc float[12],pivot=stackalloc float[12],inverse=stackalloc float[12],local=stackalloc float[12];
        EnbRetailAffineRows.Rows(ownerMatrix,owner);EnbRetailAffineRows.Rows(queriedPivotMatrix,pivot);
        EnbRetailAffineRows.RigidInverse(owner,inverse);
        EnbRetailAffineRows.Multiply(inverse,pivot,local);
        return EnbRetailAffineRows.TransformBox(local,center,extent);
    }
    /// <summary>The local sphere center through the owner rows; the radius
    /// is copied unchanged.</summary>
    public static Vector4 TransformSelectedLocalSphere(Matrix4x4 matrix,Vector4 sphere)
    {
        Span<float> m=stackalloc float[12];EnbRetailAffineRows.Rows(matrix,m);
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        R x=(((R)sphere.Z*m[2]+(R)sphere.Y*m[1])+(R)sphere.X*m[0])+m[3];
        float y=(float)((((R)sphere.Z*m[6]+(R)sphere.Y*m[5])+(R)sphere.X*m[4])+m[7]);
        float z=(float)((((R)sphere.Z*m[10]+(R)sphere.Y*m[9])+(R)sphere.X*m[8])+m[11]);
        return new((float)x,y,z,sphere.W);
    }
}
