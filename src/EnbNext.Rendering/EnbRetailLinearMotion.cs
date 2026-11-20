using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Constant-velocity motion from start toward end; the transform faces the sampled
/// position toward the endpoint (also at and past it).</summary>
public sealed class EnbRetailLinearMotion
{
    public readonly record struct Sample(Vector3 Position, Vector3 Velocity, Matrix4x4 Transform);
    private readonly Vector3 start, end;
    private readonly float speed;
    private readonly uint started;
    public Vector3 Direction { get; }

    public EnbRetailLinearMotion(Vector3 start, Vector3 end, float speed, uint started)
    {
        this.start = start; this.end = end; this.speed = speed; this.started = started;
        float dx = end.X - start.X, dy = end.Y - start.Y, dz = end.Z - start.Z;
        float square = (dx * dx + dy * dy) + dz * dz;
        if (square != 0)
        {
            float r = EnbRetailFloat.ReciprocalSquareRoot(square);
            dx = r * dx; dy = r * dy; dz = r * dz;
        }
        Direction = new(dx, dy, dz);
    }

    public Sample At(uint time)
    {
        var velocity = new Vector3(speed * Direction.X, speed * Direction.Y, speed * Direction.Z);
        double elapsed = unchecked(time - started);
        var position = new Vector3((float)(elapsed * velocity.X) + start.X,
            (float)(elapsed * velocity.Y) + start.Y, (float)(elapsed * velocity.Z) + start.Z);
        return new(position, velocity, EnbRetailCameraLookAt.BetweenPoints(position, end));
    }
}
