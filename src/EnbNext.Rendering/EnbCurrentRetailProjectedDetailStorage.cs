using System.Numerics;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>00941AF0 projected detail: the sphere's projected area in
/// viewport units at retail PC24.</summary>
public static class EnbCurrentRetailProjectedDetailStorage
{
    /// <summary>Runs the proven effect after the camera and sphere virtuals
    /// have resolved. Viewport and projection bounds are stored retail values
    /// (left, top, right, bottom); no pixel conversion is introduced.</summary>
    public static float Compute(Vector3 cameraPosition,Vector4 viewport,Vector4 projectionBounds,Vector4 worldSphere)
    {
        // The semantic caller has already resolved both virtual getters. Keep
        // their values and the native float spill points, without allocating a
        // simulated camera/sphere/stack address space for every candidate.
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        float width=(float)(((R)viewport.Z-viewport.X)/((R)projectionBounds.Z-projectionBounds.X));
        float height=(float)(((R)viewport.W-viewport.Y)/((R)projectionBounds.W-projectionBounds.Y));
        R dx=(R)worldSphere.X-cameraPosition.X,dy=(R)worldSphere.Y-cameraPosition.Y,dz=(R)worldSphere.Z-cameraPosition.Z;
        float square=(float)(dz*dz+dy*dy+dx*dx);
        float distance=(float)R.Sqrt(square);
        R ratio=0;
        if(distance!=0&&!float.IsNaN(distance))ratio=(R)worldSphere.W/distance;
        ratio*=ratio;ratio*=height;ratio*=width;ratio*=BitConverter.UInt32BitsToSingle(0x40490fdb);
        return (float)ratio;
    }
}
