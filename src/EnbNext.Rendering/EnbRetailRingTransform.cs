using System.Numerics;

namespace EnbNext.Rendering;

public static class EnbRetailRingTransform
{
    /// <summary>Camera-facing ring placement: with flag 1 the ring keeps its position and faces
    /// along the camera's resolved third column (retail's zero-roll view basis toward
    /// position + column); otherwise the world transform is used as is.</summary>
    public static Matrix4x4 Build(Matrix4x4 world,uint flags,Vector3 cameraThirdColumn)
    {
        if((flags&1)==0)return world;
        var position=world.Translation;
        return EnbRetailViewBasis.BetweenPoints(position,position+cameraThirdColumn);
    }

    public static Matrix4x4 Build(Matrix4x4 world,uint flags,EnbResolvedRenderCamera camera)
        =>Build(world,flags,-camera.Basis.Forward);
}
