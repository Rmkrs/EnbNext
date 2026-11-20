using System.Numerics;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>
/// Retail-coordinate camera transforms for the world and final bridge pass.
/// All matrices use row-vector storage; renderer basis conversion happens later.
/// </summary>
public readonly record struct EnbBridgeCameraComposition(
    Matrix4x4 BridgeCamera, Matrix4x4 WorldCamera)
{
    private Matrix4x4 NormalWorldCamera { get; init; }
    // MainViewRenderBoundSceneObjects 0050D520 temporarily replaces the shared
    // world camera with bridge+28, draws scene+120, then restores the camera.
    public static EnbRenderClipRange ClipRange => new(1, 150);
    public const float MinimumDepth = 0;
    public const float MaximumDepth = .95f;

    public static EnbBridgeCameraComposition Create(Matrix4x4 normalWorldCamera, Matrix4x4 bridgeCamera)
        // 008C2CD0 computes normal * bridge in retail column storage.
        // Transposition reverses multiplication order in System.Numerics.
        => new(bridgeCamera, EnbRetailParticleHierarchyTransform.Compose(bridgeCamera,normalWorldCamera))
        {NormalWorldCamera=normalWorldCamera};

    public EnbBridgeCameraComposition WithShake(Vector3 logicalScreenShake, float interfaceCameraDepth = 320)
    {
        // 006E2CC0 -> 0053B670 -> 0053BF99: local camera XY, never model motion.
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        float x=logicalScreenShake.X,y=logicalScreenShake.Y;
        if(interfaceCameraDepth>.0001f)
        {
            R scale=(R)1f/(R)interfaceCameraDepth;
            x=(float)(scale*(R)x);y=(float)((R)y*scale);
        }
        //53B670 stores scaled XY separately; its Z store is overwritten with0.
        x=(float)((R)4f*(R)x);y=(float)((R)4f*(R)y);
        var bridge=BridgeCamera;
        bridge.M41=(float)(((R)bridge.M21*(R)y+(R)bridge.M31*(R)0f)+(R)x*(R)bridge.M11+(R)bridge.M41);
        bridge.M42=(float)(((R)bridge.M22*(R)y+(R)bridge.M12*(R)x)+(R)bridge.M32*(R)0f+(R)bridge.M42);
        bridge.M43=(float)(((R)bridge.M23*(R)y+(R)bridge.M13*(R)x)+(R)bridge.M33*(R)0f+(R)bridge.M43);
        //53C08F composes the stored displaced bridge matrix with the original
        //world matrix. Displacing the already-composed matrix changes rounding.
        return Create(NormalWorldCamera,bridge);
    }

    public Vector3 TransformWorldLightDirection(Vector3 direction)
        // 0050D635..0050D75B rotates each copied directional light by the
        // transpose of the saved world-camera rotation. Translation is absent.
        => Vector3.TransformNormal(direction, Matrix4x4.Transpose(WorldCamera));

    public static Matrix4x4 RetailTransform(EnbResolvedRenderCamera camera)
    {
        var r = camera.Basis.Right; var u = camera.Basis.Up; var b = -camera.Basis.Forward;
        return new(r.X, r.Y, r.Z, 0, u.X, u.Y, u.Z, 0, b.X, b.Y, b.Z, 0,
            camera.Position.X, camera.Position.Y, camera.Position.Z, 1);
    }

    public static EnbResolvedRenderCamera ResolveCamera(Matrix4x4 retailTransform,
        EnbResolvedRenderCamera projectionSource, EnbRenderClipRange clips)
    {
        // Canonical local +X forward/-Y right/+Z up -> retail -Z/+X/+Y.
        var canonicalToRetail = new Matrix4x4(0, 0, -1, 0, -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        var canonical = canonicalToRetail * retailTransform;
        if (!Matrix4x4.Decompose(canonical, out _, out var orientation, out var position) ||
            !EnbResolvedRenderCamera.TryCreate(position, orientation, projectionSource.Viewport,
                projectionSource.Projection.HorizontalFieldOfViewRadians, projectionSource.Projection.AspectRatio,
                clips, out var result)) throw new InvalidDataException("Invalid bridge camera transform or projection.");
        //944710 -> 941C20 retains all twelve words; projection consumes that
        //matrix directly. Quaternion metadata must not normalize the GPU basis.
        return result with
        {
            Basis=new(new(-retailTransform.M31,-retailTransform.M32,-retailTransform.M33),
                new(retailTransform.M11,retailTransform.M12,retailTransform.M13),
                new(retailTransform.M21,retailTransform.M22,retailTransform.M23))
        };
    }
}
