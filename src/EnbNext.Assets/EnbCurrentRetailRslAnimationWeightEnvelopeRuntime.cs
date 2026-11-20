namespace EnbNext.Assets;

/// <summary>
/// Exact ordinary current-retail Animation global-weight envelope used by
/// RenderStateEffect ActionType=Linger/Ramp. The retail Animation object keeps
/// an envelope scalar at +0x58 and a per-millisecond slope at +0x5C; its active
/// update copies the current scalar to contribution weight +0x38 before
/// advancing the scalar by the current/previous client-tick delta.
/// </summary>
public static class EnbCurrentRetailRslAnimationWeightEnvelopeRuntime
{
    public const float UnsettledValue = -1.0f;
    public const float MinimumValue = 0.0f;
    public const float MaximumValue = 1.0f;
    public const float DurationEpsilon = 1.0e-4f;

    public static EnbCurrentRetailRslAnimationWeightEnvelope FreshActivation
        => new(UnsettledValue, 0.0f);

    public static EnbCurrentRetailRslAnimationWeightEnvelope BeginLinger(
        EnbCurrentRetailRslAnimationWeightEnvelope current,
        float durationMilliseconds)
    {
        float value = IsSettledRange(current.Value) ? current.Value : MaximumValue;
        float slope = -1.0f;
        if (durationMilliseconds >= DurationEpsilon)
            slope = -1.0f / durationMilliseconds;
        return new(value, slope);
    }

    public static EnbCurrentRetailRslAnimationWeightEnvelope BeginRamp(
        EnbCurrentRetailRslAnimationWeightEnvelope current,
        float durationMilliseconds)
    {
        float value = IsSettledRange(current.Value) ? current.Value : MinimumValue;
        float slope = 1.0f;
        if (durationMilliseconds >= DurationEpsilon)
            slope = 1.0f / durationMilliseconds;
        return new(value, slope);
    }

    public static EnbCurrentRetailRslAnimationWeightUpdate Update(
        EnbCurrentRetailRslAnimationWeightEnvelope current,
        uint currentClientTickMilliseconds,
        uint previousClientTickMilliseconds)
    {
        if (!IsSettledRange(current.Value))
        {
            // The retail update treats either sentinel/underflow or ramp-in
            // overshoot as a fully weighted contribution and leaves +0x58
            // untouched. Ramp-in therefore settles at full contribution after
            // it overshoots 1.0 on a prior update.
            return new(
                ContributionWeight: MaximumValue,
                EnvelopeAfterUpdate: current,
                DeactivatedBeforeAggregateGather: false);
        }

        // Current retail subtracts the two DWORD clocks in EAX, clears the high
        // half of a temporary qword, and FILDs that qword. The delta is
        // therefore unsigned 32-bit modular elapsed time, including ordinary
        // GetTickCount wraparound.
        uint deltaMilliseconds = unchecked(currentClientTickMilliseconds - previousClientTickMilliseconds);
        float nextValue = (float)(((double)deltaMilliseconds * current.SlopePerMillisecond) + current.Value);
        bool deactivate = nextValue < MinimumValue;
        EnbCurrentRetailRslAnimationWeightEnvelope next = deactivate
            ? new EnbCurrentRetailRslAnimationWeightEnvelope(UnsettledValue, current.SlopePerMillisecond)
            : new EnbCurrentRetailRslAnimationWeightEnvelope(nextValue, current.SlopePerMillisecond);

        // Current retail updates all active states before rebuilding the HAnim
        // aggregate. If the fade-out crosses below zero, generic deactivation
        // unlinks the state during this update, so the just-written +0x38 value
        // is not gathered into that frame's aggregate.
        return new(
            ContributionWeight: current.Value,
            EnvelopeAfterUpdate: next,
            DeactivatedBeforeAggregateGather: deactivate);
    }

    private static bool IsSettledRange(float value)
        => value >= MinimumValue && value <= MaximumValue;
}

public readonly record struct EnbCurrentRetailRslAnimationWeightEnvelope(
    float Value,
    float SlopePerMillisecond);

public readonly record struct EnbCurrentRetailRslAnimationWeightUpdate(
    float ContributionWeight,
    EnbCurrentRetailRslAnimationWeightEnvelope EnvelopeAfterUpdate,
    bool DeactivatedBeforeAggregateGather);
