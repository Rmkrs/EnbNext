using System.Numerics;
using Register=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>Whole00988AD0/988EC0/989060 hierarchy loading over retained
/// header, record and caller scratch storage and the concrete chunk reader.</summary>
public static class EnbCurrentRetailHierarchyLoadStorage
{
    /// <summary>0098917B..989292 authored local transform, including
    /// identity translation arithmetic and the actual quaternion product.</summary>
    public static Matrix4x4 AuthoredLocal(Vector3 translation,Quaternion rotation,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
        Register x=translation.X,y=translation.Y,z=translation.Z;
        var translated=Matrix4x4.Identity;
        translated.M41=(float)(z*0+y*0+x*1+0);
        translated.M42=(float)(z*0+y*1+x*0+0);
        translated.M43=(float)(z*1+y*0+x*0+0);
        return EnbRetailPointGroupTransform.Compose(EnbRetailParticleHierarchyTransform.QuaternionBasis(rotation,precision),translated,precision);
    }
}
