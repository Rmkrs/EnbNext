using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Station-local motion; retail 00783B50 and 0063FF80.</summary>
public sealed class EnbStationMovement(Matrix4x4 spawn)
{
    public Vector3 Position { get; private set; } = spawn.Translation;
    public float Heading { get; private set; } = MathF.Atan2(spawn.M12, spawn.M11);
    public float Speed { get; private set; }
    public int Thrust { get; set; }
    public float Turn { get; set; }
    public float Look { get; set; }
    public float CameraPitch { get; private set; }
    public float CameraYaw { get; private set; }
    public float CameraTurn { get; set; }
    public void RestoreCamera() { CameraPitch=0;CameraYaw=0; }
    public bool Run { get; set; }
    /// <summary>
    /// Next free station movement (owner-approved option; retail turns and walks straight): the wished direction as
    /// (right, forward) seen from the camera, or null for retail movement. In third person the avatar turns to face
    /// the way it goes (the camera keeps its direction) and walks or runs forward, or walks backward when only backing
    /// up; in first person it moves that way facing the camera. Only the position and facing it sends change.
    /// </summary>
    public Vector2? Free { get; set; }
    /// <summary>First person: free movement steps any way without turning (the camera is the avatar's view).</summary>
    public bool FreeFirstPerson { get; set; }
    /// <summary>
    /// Next free movement: the mouse turns the view by yaw and tilts it by pitch (radians). With <paramref name="avatar"/>
    /// the avatar turns to face the view at once (holding the right button, as in other games); else only the view
    /// swings around it.
    /// </summary>
    public void TurnFree(float yaw, float pitch, bool avatar)
    {
        CameraPitch = Math.Clamp(CameraPitch + pitch, -.8f, .4f);
        if (avatar) { Heading = MathF.IEEERemainder(Heading + CameraYaw + yaw, MathF.Tau); CameraYaw = 0; }
        else CameraYaw = MathF.IEEERemainder(CameraYaw + yaw, MathF.Tau);
    }
    private bool FreeMoving => Free is { } wish && wish != Vector2.Zero;
    private bool FreeBackward => Free is { } wish && wish.Y < 0 && wish.X == 0;
    /// <summary>The movement animation's direction: forward 1, backward -1, standing 0.</summary>
    public int Moving => FreeMoving ? (FreeBackward ? -1 : 1) : Thrust;
    private double _sendRemaining;
    public Matrix4x4 Transform => Matrix4x4.CreateRotationZ(Heading) * Matrix4x4.CreateTranslation(Position);
    public void Stop() { Thrust = 0; Turn = 0; Look = 0; CameraTurn=0; Speed = 0; Free = null; }
    public void Relocate(Matrix4x4 spawn)
    {
        Position=spawn.Translation;
        Heading=MathF.Atan2(spawn.M12,spawn.M11);
        _sendRemaining=0;
    }
    public bool Advance(double seconds, Func<Vector3, Vector3, Vector3> collide)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return false;
        float dt = (float)Math.Min(seconds, .25);
        // 00783DF1..00783E5B: signed +300 input, 1 radian/sec,
        // clamp +30C to [-0.8, 0.4]; a long frame contributes no rotation.
        if (seconds <= .25) CameraPitch = Math.Clamp(CameraPitch + Math.Clamp(Look,-1,1) * dt, -.8f, .4f);
        // 00783E65..00783EFB: separate signed yaw, two radians/sec;
        // unlike heading/pitch it still advances by capped dt on long frames.
        CameraYaw+=2*Math.Clamp(CameraTurn,-1,1)*dt;
        float turn = Math.Clamp(Turn, -1, 1);
        // The mouse input supplies distance from center (0..0.5), keyboard 1.
        float turnScale = Math.Min(Math.Abs(turn) * 4, 1);
        if (seconds <= .25) Heading = MathF.IEEERemainder(Heading - Math.Sign(turn) * turnScale * 2 * dt, MathF.Tau);
        float acceleration = 30 * dt * (Speed > 0 && Run ? 2.5f : 1);
        int thrust = Moving;
        float target = thrust > 0 ? (Run ? 25 : 10) : thrust < 0 ? -10 : 0;
        Speed = target > Speed ? Math.Min(target, Speed + acceleration) : Math.Max(target, Speed - acceleration);
        float course = Heading;
        if (FreeMoving)
        {
            var wish = Free!.Value;
            // The camera looks along the heading turned by its own yaw (EnbRetailStationCameraTransform).
            float view = FreeFirstPerson ? Heading : Heading + CameraYaw;
            course = FreeBackward ? view : view + MathF.Atan2(-wish.X, wish.Y);
            if (!FreeFirstPerson)
            {
                // Turn to face the way (quickly, as a person would); the camera keeps looking where it looked.
                float want = FreeBackward ? view : course;
                float step = Math.Clamp(MathF.IEEERemainder(want - Heading, MathF.Tau), -10 * dt, 10 * dt);
                Heading = MathF.IEEERemainder(Heading + step, MathF.Tau);
                CameraYaw = MathF.IEEERemainder(CameraYaw - step, MathF.Tau);
            }
            // Backing up keeps facing the camera and steps back (the speed is then negative).
        }
        var delta = new Vector3(MathF.Cos(course), MathF.Sin(course), 0) * Speed * dt;
        Position = collide(Position, delta);
        if (thrust == 0 && Turn == 0) { _sendRemaining = 0; return false; }
        _sendRemaining -= seconds;
        if (_sendRemaining >= 0) return false;
        _sendRemaining = .5;
        return true;
    }
}
