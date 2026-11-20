using System.Numerics;
using EnbNext.Net7.Timing;
using EnbNext.Net7.World;

namespace EnbNext.Net7.Gameplay;

/// <summary>
/// Clean movement compatibility state: independent steering
/// throttles plus the current 0x1004 snapshot comparator/countdown semantics.
/// </summary>
public sealed class Net7MovementFeedbackController
{
    private readonly INet7RuntimeClock _clock;
    private uint? _lastHorizontalAccepted;
    private uint? _lastVerticalAccepted;
    private Vector3 _lastPosition;
    private Vector3 _lastOrientationColumn0;
    private ushort _fullSnapshotCountdown;
    private int _legacyPositionFeedbackSpeed;
    private int _cockpitNumericComparisonValue;

    public Net7MovementFeedbackController(INet7RuntimeClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Dedicated legacy 0x1004 feedback-speed value. This is the last
    /// numeric signed-speed value owned by the position-feedback path, not a
    /// generic/UI-displayed speed. Retail LockSpeed display overrides did not
    /// update the bridge-equivalent value.
    /// </summary>
    public int LegacyPositionFeedbackSpeed
    {
        get => _legacyPositionFeedbackSpeed;
        set
        {
            // Explicit seeding is a compatibility/test seam. Keep the hidden
            // retail cockpit comparison value coherent with the mirrored value.
            _legacyPositionFeedbackSpeed = value;
            _cockpitNumericComparisonValue = value;
        }
    }

    public bool TryAcceptHorizontal(float intensity)
        => TryAcceptSteering(intensity, ref _lastHorizontalAccepted);

    public bool TryAcceptVertical(float intensity)
        => TryAcceptSteering(intensity, ref _lastVerticalAccepted);

    public Net7PositionFeedbackSample? Capture(Net7WorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);
        uint? localId = world.LocalShipObjectId;
        if (!localId.HasValue || !world.TrySampleObject(localId.Value, _clock.GetMilliseconds(), out Net7SpatialSample spatial))
            return null;
        if (spatial.Position == Vector3.Zero)
            return null;

        Vector3 column0 = spatial.OrientationColumn0;
        ObserveLegacyPositionFeedbackKinematics(spatial.Velocity, column0, world.LocalShipLockSpeedActive);
        return new Net7PositionFeedbackSample(spatial.Position, column0, _legacyPositionFeedbackSpeed);
    }

    public bool TrySelectSnapshot(Net7PositionFeedbackSample sample, out bool full)
    {
        if (!SameBits(sample.OrientationColumn0, _lastOrientationColumn0))
        {
            _lastPosition = sample.Position;
            _lastOrientationColumn0 = sample.OrientationColumn0;
            _fullSnapshotCountdown = 2;
            full = true;
            return true;
        }

        if (!SameBits(sample.Position, _lastPosition))
        {
            _lastPosition = sample.Position;
            _fullSnapshotCountdown = 2;
            full = false;
            return true;
        }

        if (_fullSnapshotCountdown != 0)
        {
            _fullSnapshotCountdown--;
            full = true;
            return true;
        }

        full = false;
        return false;
    }

    public void ResetSectorState()
    {
        // Both authoritative steering throttles live on the
        // retail role-3 Gameplay Connection itself (horizontal +0xCFAC,
        // vertical +0xCFA8). A sector redirect replaces that connection with a
        // freshly constructed one, so neither timestamp can bleed into the
        // next sector.
        _lastHorizontalAccepted = null;
        _lastVerticalAccepted = null;

        // Snapshot caches/countdown belong to the long-lived
        // master/control MVAS owner, not the replaceable role-3 Gameplay
        // connection. Retail worker startup zeroes the caches once; a sector
        // redirect does not reset them.
    }

    internal void ObserveLegacyPositionFeedbackKinematics(
        Vector3 velocity,
        Vector3 orientationColumn0,
        bool lockSpeed)
    {
        // 005821C0's LockSpeed branch writes the cockpit comparison/display field
        // to 100 but bypasses the proxy mirror. The next normal numeric update
        // mirrors only when its newly computed integer differs from that field.
        if (lockSpeed)
        {
            _cockpitNumericComparisonValue = 100;
            return;
        }

        // 005821C0: speed * 1000, negated against orientation column 0, then __ftol.
        float magnitude = velocity.Length();
        float scaled = magnitude * 1000f;
        float signed = Vector3.Dot(velocity, orientationColumn0) < 0f ? -scaled : scaled;
        // __ftol's FISTP stores the x87 integer indefinite for NaN or out-of-range values.
        // int.MaxValue is not exactly representable as float; 2^31 is the first out-of-range value.
        int numeric = signed >= -2147483648f && signed < 2147483648f ? (int)MathF.Truncate(signed) : int.MinValue;
        if (numeric == _cockpitNumericComparisonValue)
            return;

        _cockpitNumericComparisonValue = numeric;
        _legacyPositionFeedbackSpeed = numeric;
    }

    private bool TryAcceptSteering(float intensity, ref uint? lastAccepted)
    {
        uint now = _clock.GetMilliseconds();
        if (intensity != 0f && lastAccepted is uint last && !ElapsedStrictlyMoreThan(now, last, 250u))
            return false;
        lastAccepted = now;
        return true;
    }

    private static bool ElapsedStrictlyMoreThan(uint now, uint then, uint interval)
        => unchecked(now - then) > interval;

    private static bool SameBits(Vector3 a, Vector3 b)
        => BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
            && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
            && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);
}
