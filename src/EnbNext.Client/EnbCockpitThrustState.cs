using System.Globalization;
using System.Numerics;
using EnbNext.Presentation;

namespace EnbNext.Client;

/// <summary>Local cockpit click-lock state, separate from held keyboard and RMB input.</summary>
public sealed class EnbCockpitThrustState
{
    public int Direction { get; private set; }
    public bool Locked { get; private set; }
    public bool UseLockedMove { get; private set; }
    private long? _pressedAt;
    public void Press(int direction, bool clickLocks, long now = 0)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        // 00583790: a click while either button is locked stops first.
        if (Locked) { Clear(); return; }
        Direction = direction; Locked = UseLockedMove = clickLocks; _pressedAt = now;
    }
    public void AdvanceHeld(long now, bool inside)
    {
        if (_pressedAt is not long start || Locked) return;
        if (!inside) { _pressedAt = now; return; }
        // 00673F80 accumulates milliseconds * .001; 006740E0 uses > 1 second.
        // 00583790 promotes the local latch without emitting another thrust command.
        if (now - start > 1000) Locked = true;
    }
    public void Release() { _pressedAt = null; if (!Locked) Clear(); }
    public void ApplyKeyboardLock(int direction, bool toggle, int currentDirection)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        // 005806E0 commands 10/11/12: opposite lock stops; same lock is
        // idempotent except forward toggle. Held-direction promotion sends no new Move.
        if (Locked)
        {
            if (Direction != direction || toggle) Clear();
            return;
        }
        Direction = direction;
        Locked = true;
        UseLockedMove = currentDirection != direction;
        _pressedAt = null;
    }
    public void Clear() { Direction = 0; Locked = UseLockedMove = false; _pressedAt = null; }
    public static string SpeedText(IEnbRenderTransform? transform)
    {
        if (transform is null) return "000";
        float speed = transform.Velocity.Length() * 1000;
        if (Vector3.Dot(transform.Velocity, transform.OrientationColumn0) < 0) speed = -speed;
        if (!float.IsFinite(speed)) return "000";
        int value = (int)Math.Clamp((double)speed, int.MinValue, int.MaxValue);
        // printf %03d counts the sign in its minimum width.
        return value < 0 ? "-" + (-(long)value).ToString("D2", CultureInfo.InvariantCulture)
            : value.ToString("D3", CultureInfo.InvariantCulture);
    }
}
