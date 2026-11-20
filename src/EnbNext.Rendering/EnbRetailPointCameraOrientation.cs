using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Retail point camera: the camera at the origin looks at the target with world Z as the
/// up reference. The basis keeps retail's camera-local axes (right, up, back).</summary>
public static class EnbRetailPointCameraOrientation
{
    public readonly record struct Result(Vector3 Position, Quaternion Orientation, Matrix4x4 LookAtMatrix);
    private const float EmoteDistance = 75f * 4f;

    public static EnbRenderCameraBasis CameraBasis(Quaternion native)
    {
        var m=EnbRetailCameraQuaternionBasis.Create(native);
        return new(-new Vector3(m.M31,m.M32,m.M33),new(m.M11,m.M12,m.M13),new(m.M21,m.M22,m.M23));
    }
    public static Quaternion CanonicalOrientation(Quaternion native)
    {
        var b=CameraBasis(native);
        return Quaternion.CreateFromRotationMatrix(new(b.Forward.X,b.Forward.Y,b.Forward.Z,0,
            -b.Right.X,-b.Right.Y,-b.Right.Z,0,b.Up.X,b.Up.Y,b.Up.Z,0,0,0,0,1));
    }
    public static Quaternion FromCanonicalOrientation(Quaternion canonical)
    {
        EnbRenderCameraBasis.TryCreate(canonical,out var b);
        return EnbCurrentRetailParticleBirthTransform.ExtractOrientation(new(b.Right.X,b.Right.Y,b.Right.Z,0,
            b.Up.X,b.Up.Y,b.Up.Z,0,-b.Forward.X,-b.Forward.Y,-b.Forward.Z,0,0,0,0,1));
    }

    public static Result Evaluate(Vector3 origin, Vector3 target)
    {
        float fx = target.X - origin.X, fy = target.Y - origin.Y, fz = target.Z - origin.Z;
        Normalize(ref fx, ref fy, ref fz, (fx * fx + fz * fz) + fy * fy);
        // Right = forward x world up (0, 0, 1).
        float rx = fy * 1f - 0f * fz, ry = fz * 0f - fx * 1f, rz = 0f * fx - fy * 0f;
        Normalize(ref rx, ref ry, ref rz, (rx * rx + rz * rz) + ry * ry);
        float ux = ry * fz - rz * fy, uy = rz * fx - fz * rx, uz = fy * rx - ry * fx;
        float square = (ux * ux + uz * uz) + uy * uy;
        if (square != 0)
        {
            float r = EnbRetailFloat.ReciprocalSquareRoot(square);
            ux *= r; uy *= r; uz *= r;
        }
        var m = new Matrix4x4(rx, ry, rz, 0, ux, uy, uz, 0, -fx, -fy, -fz, 0, origin.X, origin.Y, origin.Z, 1);
        return new(origin, EnbRetailQuaternionConversions.ExtractOrientation(m), m);
    }

    /// <summary>Ship-emote camera start: a previous camera closer than 300 units is pushed out to
    /// 300 units along the line from the ship; a farther one stays where it was.</summary>
    public static Vector3 DistancePosition(Vector3 previousCamera, Vector3 shipPosition)
    {
        float dx = shipPosition.X - previousCamera.X, dy = shipPosition.Y - previousCamera.Y, dz = shipPosition.Z - previousCamera.Z;
        float square = (dz * dz + dy * dy) + dx * dx;
        if (!(square < EmoteDistance * EmoteDistance)) return previousCamera;
        Normalize(ref dx, ref dy, ref dz, square);
        return new(shipPosition.X - dx * EmoteDistance, shipPosition.Y - dy * EmoteDistance, shipPosition.Z - dz * EmoteDistance);
    }

    private static void Normalize(ref float x, ref float y, ref float z, float square)
    {
        if (square == 0) return;
        float r = EnbRetailFloat.ReciprocalSquareRoot(square);
        x *= r; y *= r; z *= r;
    }
}
