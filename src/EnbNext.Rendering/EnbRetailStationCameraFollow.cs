using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>0063E930: physical room sweep and retained third-person camera.</summary>
public static class EnbRetailStationCameraFollow
{
    public static Vector3 SweepOrigin(Vector3 avatar,float modelHeadHeight)
        =>new(avatar.X,avatar.Y,(avatar.Z+(modelHeadHeight+1))+1);

    public static Matrix4x4 Apply(Matrix4x4 desired,Vector3 origin,Vector3 previous,bool hit,float fraction)
    {
        if(hit)desired.Translation=(origin+(desired.Translation-origin)*fraction+previous)*.5f;
        return desired;
    }
}
