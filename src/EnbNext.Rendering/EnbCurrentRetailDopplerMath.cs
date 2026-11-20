using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>
/// Arithmetic kernel of current client.exe 009F8920. Inputs are world units per
/// millisecond; toListener is listener position minus source position. This does
/// not own sound callbacks, clocks, transforms, or authored-limit initialization.
/// </summary>
public static class EnbCurrentRetailDopplerMath
{
    public static EnbCurrentRetailDopplerStep Step(Vector3 sourceVelocity, Vector3 listenerVelocity,
        Vector3 toListener, Vector3 previousFilteredMotion, float previousPitch, float elapsedMilliseconds,
        float tweak, float minimumPitch, float maximumPitch)
    {
        if (!Finite(sourceVelocity) || !Finite(listenerVelocity) || !Finite(toListener) || !Finite(previousFilteredMotion) ||
            !float.IsFinite(previousPitch) || previousPitch < 0 || !float.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < 0 ||
            !float.IsFinite(tweak) || !float.IsFinite(minimumPitch) || !float.IsFinite(maximumPitch) ||
            minimumPitch < 0 || maximumPitch < minimumPitch)
            throw new ArgumentOutOfRangeException(nameof(sourceVelocity), "Doppler inputs must be finite with ordered non-negative pitch limits.");

        Vector3 relative = sourceVelocity - listenerVelocity;
        if (!Finite(relative)) throw new ArgumentOutOfRangeException(nameof(sourceVelocity), "Relative velocity overflowed.");
        float cosine = Vector3.Dot(NormalizeOrZero(relative), NormalizeOrZero(toListener));
        // Retail weights the relative VECTOR by its direction cosine before
        // filtering; replacing this with a smoothed scalar radial speed changes turns.
        Vector3 filtered = .85f * (relative * cosine) + (1f - .85f) * previousFilteredMotion;
        if (!Finite(filtered)) throw new ArgumentOutOfRangeException(nameof(previousFilteredMotion), "Filtered velocity overflowed.");
        float speed = Math.Min((filtered * tweak).Length(), 3.4f);
        float denominator = cosine > 0 ? 3.4f - speed : 3.4f + speed;
        if (denominator == 0) denominator = 1;
        float desiredPitch = 3.4f / denominator;
        // Raw 009F8C69..009F8CDE: slew first, then absolute authored bounds.
        float maximumChange = elapsedMilliseconds * .05f * BitConverter.Int32BitsToSingle(0x3CF6018A);
        float pitch = Math.Clamp(desiredPitch, previousPitch - maximumChange, previousPitch + maximumChange);
        pitch = Math.Clamp(pitch, minimumPitch, maximumPitch);
        return new(filtered, cosine, desiredPitch, pitch);
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static Vector3 NormalizeOrZero(Vector3 value)
    {
        if (value == Vector3.Zero) return Vector3.Zero;
        // Modern normalization, not the legacy reciprocal-square-root bit hack.
        double length = Math.Sqrt((double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z);
        return new((float)(value.X / length), (float)(value.Y / length), (float)(value.Z / length));
    }
}

public readonly record struct EnbCurrentRetailDopplerStep(Vector3 FilteredMotion, float DirectionCosine, float DesiredPitch, float Pitch);
