using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>The direct-scene MFD scanner selected by client.exe 005BEA00.
/// Camera update: 005C2F00 -> 005C2920; roll-zero look-at: 008C2780.</summary>
public static class EnbCurrentRetailScannerCameraRuntime
{
    /// <summary>
    /// Converts an immutable-asset HLOD bound to the geometry-owned scale used
    /// by retail's scanner proxy (+F8: 004804A0). MeshModel 0092D460 scales its
    /// bounds before that getter; 009421B0 only rigidly places their center.
    /// The supplied center is already world-transformed. Pass 1 when the bounds
    /// compiler already incorporated model scale (ShipLego post-transforms).
    /// </summary>
    public static bool TryResolveFitSphere(EnbW3dWorldBounds worldSphere,
        float unappliedModelScale, out EnbW3dWorldBounds sphere)
    {
        sphere = worldSphere with { Radius = worldSphere.Radius * unappliedModelScale };
        return worldSphere.IsValid && float.IsFinite(unappliedModelScale) &&
            unappliedModelScale > 0 && sphere.IsValid;
    }

    public static bool TryCreate(Vector3 observerPosition, Vector3 objectPosition,
        Vector3 sphereCenter, float sphereRadius, EnbRenderViewport viewport,
        double authoredAspectRatio, out EnbResolvedRenderCamera camera,
        EnbRetailScannerCameraState? retainedState=null)
    {
        camera = default;
        Vector3 delta = sphereCenter - observerPosition;
        if (!IsFinite(delta) || !IsFinite(objectPosition) || !IsFinite(sphereCenter) ||
            !float.IsFinite(sphereRadius) || sphereRadius <= 0 ||
            !double.IsFinite(authoredAspectRatio) || authoredAspectRatio <= 0)
            return false;

        var state=(retainedState??new()).Update(observerPosition,objectPosition,sphereCenter,sphereRadius);
        var native=state.Transform;
        Vector3 forward=-new Vector3(native.M31,native.M32,native.M33);
        Vector3 right=new(native.M11,native.M12,native.M13);
        Vector3 left=-right;
        Vector3 up=new(native.M21,native.M22,native.M23);
        Quaternion orientation = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            forward.X, forward.Y, forward.Z, 0,
            left.X, left.Y, left.Z, 0,
            up.X, up.Y, up.Z, 0,
            0, 0, 0, 1));
        Vector3 position = new(native.M41,native.M42,native.M43);
        // The view plane is y=[-.5,.5], x=+/-.5 * authored width/height.
        // Its aspect is authored UI geometry, not a widescreen pixel ratio.
        if(!EnbResolvedRenderCamera.TryCreate(position, orientation, viewport,
            2 * Math.Atan(0.5 * authoredAspectRatio), authoredAspectRatio,
            new(state.Near,state.Far), out camera))return false;
        // Retail submits the matrix directly; metadata quaternion conversion must
        // not normalize/reconstruct the basis used by rendering and projection.
        camera=camera with {Basis=new(forward,right,up)};
        return true;
    }

    private static bool IsFinite(Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
