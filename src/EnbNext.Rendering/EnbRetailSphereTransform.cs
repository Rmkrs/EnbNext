using System.Numerics;

namespace EnbNext.Rendering;

public static class EnbRetailSphereTransform
{
    // Equivalent world-space form of 0090C530..0090CE2B. Header center is
    // a bounds input, not a geometry translation in this draw path.
    public static Matrix4x4 Build(Matrix4x4 objectWorld,Vector3 authoredExtent,
        Vector3 scaleMultiplier,bool faceCamera,EnbRenderCameraBasis camera)
    {
        var scale=authoredExtent*scaleMultiplier;
        if(!faceCamera)return Matrix4x4.CreateScale(scale)*objectWorld;
        var x=-camera.Forward*scale.X;
        var y=camera.Right*scale.Y;
        var z=camera.Up*scale.Z;
        var p=objectWorld.Translation;
        return new(x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,p.X,p.Y,p.Z,1);
    }
}
