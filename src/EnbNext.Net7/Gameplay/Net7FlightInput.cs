namespace EnbNext.Net7.Gameplay;

public readonly record struct Net7FlightInput(float Horizontal, float Vertical, int Thrust)
{
    public bool ThrustLocked { get; init; }
    public byte MoveType => Thrust switch { 1 => ThrustLocked ? (byte)2 : (byte)0,
        -1 => ThrustLocked ? (byte)3 : (byte)1, _ => (byte)4 };
    public void Validate()
    {
        if (!float.IsFinite(Horizontal) || MathF.Abs(Horizontal) > 1 ||
            !float.IsFinite(Vertical) || MathF.Abs(Vertical) > 1 || Thrust is < -1 or > 1 || (Thrust == 0 && ThrustLocked))
            throw new ArgumentOutOfRangeException(nameof(Net7FlightInput));
    }
}
