namespace EnbNext.Assets;

/// <summary>
/// Bounded current-retail 3D Sound submission semantics recovered from
/// client.exe. Authored sounds.ini values remain inputs; this layer only maps
/// the proven runtime treatment of those values into backend-neutral concepts.
/// </summary>
public static class EnbCurrentRetailRsl3dSoundRuntime
{
    public static bool TryCreatePlan(
        EnbRetailSoundDefinition definition,
        EnbRetailSoundResolution resolution,
        EnbCurrentRetailRslSoundContract contract,
        out EnbCurrentRetailRsl3dSoundPlan plan,
        out EnbRetailSoundResolvedChoice? selectedChoice,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(contract);

        plan = default!;
        selectedChoice = null;
        diagnostic = null;

        if (!contract.InstanceHandleEligible)
        {
            diagnostic = "current-retail live Sound instance is not handle-eligible.";
            return false;
        }

        if (contract.HandleLoopCountOverride is not (0 or 1))
        {
            diagnostic =
                $"current-retail RSL loop control {contract.HandleLoopCountOverride} is outside the proven zero/one handle contract.";
            return false;
        }

        if (resolution.Choices.Count != 1)
        {
            diagnostic = $"current-retail weighted/NULL selection has {resolution.Choices.Count} choices; R6Z27 does not invent the random-selection cursor.";
            return false;
        }

        EnbRetailSoundResolvedChoice choice = resolution.Choices[0];
        if (choice.Status is not (EnbRetailSoundResourceStatus.LooseFile or EnbRetailSoundResourceStatus.MixResource))
        {
            diagnostic = $"selected sound resource is {choice.Status}, not one exact readable loose/MIX resource.";
            return false;
        }

        if (choice.Choice.Weight.HasValue)
        {
            // One weighted entry has no stochastic ambiguity, but preserving this
            // distinction in the plan keeps authored \Files topology observable.
            if (choice.Choice.Weight.Value <= 0)
            {
                diagnostic = "selected weighted sound resource has a non-positive authored weight.";
                return false;
            }
        }

        if (!IsZeroShift(definition.EffectiveMinVolumeShift) ||
            !IsZeroShift(definition.EffectiveMaxVolumeShift) ||
            !IsZeroShift(definition.EffectiveMinPitchShift) ||
            !IsZeroShift(definition.EffectiveMaxPitchShift))
        {
            diagnostic = "current-retail randomized volume/pitch shifts are outside the bounded R6Z27 submission boundary.";
            return false;
        }

        if (definition.EffectiveSecondsBetweenCalls != 0f)
        {
            diagnostic = "current-retail SecondsBetweenCalls rate limiting is outside the bounded R6Z27 submission boundary.";
            return false;
        }

        // 0043E360 forwards SoundType to the handle (+90) without filtering.
        // This non-Doppler factory never reads the Doppler limits at +48/+4C;
        // those belong to the separate moving-sound path, not admission here.

        float gain = definition.EffectiveVolume;
        float pitch = definition.EffectivePitch;
        float priority = definition.EffectivePriority;
        float referenceDistance = Math.Max(1f, definition.EffectiveMaxVolumeRadius);
        float maximumDistance = definition.EffectiveDropOffRadius;

        if (!float.IsFinite(gain) || gain < 0f ||
            !float.IsFinite(pitch) || pitch <= 0f ||
            !float.IsFinite(priority) ||
            !float.IsFinite(referenceDistance) || !float.IsFinite(maximumDistance) ||
            maximumDistance < referenceDistance)
        {
            diagnostic =
                $"sound gain/pitch/radii are not backend-safe under the current-retail mapping: gain={gain}, pitch={pitch}, ref={referenceDistance}, max={maximumDistance}.";
            return false;
        }

        if (definition.EffectiveMaxInstances <= 0)
        {
            diagnostic = $"SoundMaxInstances {definition.EffectiveMaxInstances} cannot admit a live handle.";
            return false;
        }

        // Current client passes sounds.ini DropOffRadius to Miles maximum distance
        // and MaxVolRadius to Miles minimum/full-volume distance (clamped to >=1).
        // The RSL state then overwrites the handle loop count. Miles' public loop
        // count contract gives zero as continuous looping and one as one pass.
        plan = new EnbCurrentRetailRsl3dSoundPlan(
            Gain: gain,
            Pitch: pitch,
            ReferenceDistance: referenceDistance,
            MaximumDistance: maximumDistance,
            LoopContinuously: contract.HandleLoopCountOverride == 0,
            FollowsLiveObjectTransform: contract.FollowsLiveObjectTransform,
            StopsOnCleanup: contract.StopsHandleOnCleanup,
            MaximumInstances: definition.EffectiveMaxInstances,
            SoundType: definition.EffectiveSoundType,
            Priority: priority);
        selectedChoice = choice;
        return true;
    }

    public static bool TryValidateDecodedSpatialPcm(
        EnbRetailWavePcm16 pcm,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        diagnostic = null;

        if (pcm.Channels != 1)
        {
            diagnostic =
                $"decoded sound has {pcm.Channels} channels; R6Z27 admits mono only for positional 3D submission.";
            return false;
        }

        if (pcm.SampleRate <= 0 || pcm.FrameCount <= 0 || pcm.Pcm16LittleEndian.Length == 0)
        {
            diagnostic =
                $"decoded mono sound is not a complete playable PCM stream: rate={pcm.SampleRate}, frames={pcm.FrameCount}, bytes={pcm.Pcm16LittleEndian.Length}.";
            return false;
        }

        return true;
    }

    private static bool IsZeroShift(float value)
        => float.IsFinite(value) && Math.Abs(value) <= 0.000001f;

}

public sealed record EnbCurrentRetailRsl3dSoundPlan(
    float Gain,
    float Pitch,
    float ReferenceDistance,
    float MaximumDistance,
    bool LoopContinuously,
    bool FollowsLiveObjectTransform,
    bool StopsOnCleanup,
    int MaximumInstances,
    int SoundType,
    float Priority);
