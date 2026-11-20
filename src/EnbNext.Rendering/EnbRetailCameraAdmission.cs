using System.Numerics;
using X87 = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>799E40 warp/loot admission, including the native stores and C0 comparisons.</summary>
public static class EnbRetailCameraAdmission
{
    private static float Distance(Vector3 source,Vector3 target)
    {
        X87 x=(X87)target.X-source.X, y=(X87)target.Y-source.Y, z=(X87)target.Z-source.Z;
        float squared=(float)((z*z+y*y)+x*x); // 79A075..79A085
        return (float)X87.Sqrt(squared);
    }

    public static bool NearWarpDestination(Vector3 source, Vector3 target, float radius)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        float distance=Distance(source,target);
        X87 separation=(X87)distance-((X87)radius+75);
        return (X87.CompareFloatMemory(separation,0x466a6000)&0x100)!=0;
    }

    //797B50: both travel-to-approach branches. Placement consumes the original
    //distance or unclamped speed; the threshold alone uses the speed floor.
    public static bool CanStartWarpApproach(Vector3 source,Vector3 target,Vector3 velocity,float radius,bool final,
        out float distance,out float speed)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        distance=Distance(source,target);
        float remaining=final?(float)((X87)distance-((X87)radius+75)):distance;
        speed=EnbRetailWarpCameraPlacement.Speed(velocity);
        X87 denominator=(X87.CompareFloatMemory(speed,0x3a83126f)&0x4100)!=0 ? .001f : speed;
        X87 numerator=final?(X87)remaining-1000:remaining;
        return (X87.CompareFloatMemory(numerator/denominator,0x44fa0000)&0x100)!=0;
    }

    public static bool LootTargetEligible(Vector3 source, Vector3 target, float radius)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        X87 x=(float)((X87)target.X-source.X), y=(float)((X87)target.Y-source.Y), z=(float)((X87)target.Z-source.Z);
        X87 squared=(z*z+x*x)+y*y; // 79AB4D..79AB63, not Vector3.Dot order
        X87 limit=(X87)radius*radius+5625;
        limit+=limit;
        return (X87.CompareRegisters(limit,squared)&0x100)!=0;
    }

    public static bool ReturnFromLoot(Vector3 source,Vector3 target,float radius)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        X87 x=(float)((X87)target.X-source.X), y=(float)((X87)target.Y-source.Y), z=(float)((X87)target.Z-source.Z);
        X87 squared=(z*z+y*y)+x*x; //797644..79765E
        X87 limit=(X87)75*75+(X87)radius*radius;
        return (X87.CompareRegisters(limit,squared)&0x4100)==0;
    }
}
