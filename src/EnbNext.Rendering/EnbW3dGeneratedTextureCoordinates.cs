using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>
/// Backend-neutral reference for the deterministic generated-coordinate mapper
/// semantics realized. It mirrors the current D3D8 TCI source domain,
/// including +Z-forward camera space, retained LOCALVIEWER=TRUE reflection math,
/// and NORMALIZENORMALS=FALSE behavior. Production GPU code implements the same
/// algebra in the vertex shader; this reference pins offline conformance.
/// </summary>
public static class EnbW3dGeneratedTextureCoordinates
{
    public static Matrix4x4 CreateRetailCameraViewFromWorld(EnbResolvedRenderCamera camera)
    {
        EnbRenderCameraBasis basis = camera.Basis;
        Vector3 position = camera.Position;
        return new Matrix4x4(
            basis.Right.X, basis.Up.X, basis.Forward.X, 0f,
            basis.Right.Y, basis.Up.Y, basis.Forward.Y, 0f,
            basis.Right.Z, basis.Up.Z, basis.Forward.Z, 0f,
            -Vector3.Dot(position, basis.Right),
            -Vector3.Dot(position, basis.Up),
            -Vector3.Dot(position, basis.Forward),
            1f);
    }
}
