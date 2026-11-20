using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>00787A90/00786EB0: current body pivots and stored model head height.</summary>
public static class EnbRetailStationCameraTransform
{
    public static Matrix4x4 Build(Matrix4x4 reference,float currentHeadZ,float modelHeadHeight,float pitch,float yaw,bool thirdPerson)
    {
        var axes=reference;axes.Translation=Vector3.Zero;
        if(thirdPerson)axes=Matrix4x4.CreateRotationZ(yaw)*axes;
        var forward=new Vector3(axes.M11,axes.M12,axes.M13);
        var position=reference.Translation+forward*(thirdPerson?-11:.5f);
        position.Z=thirdPerson?reference.Translation.Z+modelHeadHeight+2-forward.Z*11:currentHeadZ+.2f+forward.Z*.5f;
        var camera=Matrix4x4.CreateRotationY((thirdPerson?.3f:0)+pitch)*axes;
        camera.Translation=position;
        return camera;
    }
}
