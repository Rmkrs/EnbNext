using EnbNext.Assets;

namespace EnbNext.Rendering;

public readonly record struct EnbRslAnimationActivationInterval(
    uint ActivationClientTime,
    uint? PacketActivateNextOnceClientTime)
{
    public bool PacketActivateNextOnceArmed => PacketActivateNextOnceClientTime.HasValue;
}

/// <summary>
/// Current-retail RSL HAnim naming, template-flag and frame helpers. Activation,
/// NEXT and LINK execution belong to the retained RSL object runtime.
/// </summary>
public static class EnbCurrentRetailRslAnimationRuntime
{
    public static string BuildHAnimName(
        string hierarchyIdentity,
        string? authoredAnimation,
        string? authoredSuffix)
    {
        ArgumentNullException.ThrowIfNull(hierarchyIdentity);

        string animation = authoredAnimation ?? string.Empty;
        string suffix = authoredSuffix ?? string.Empty;

        // Current client FUN_00459D50 first calls RenderObj virtual +0xCC.
        // For HLOD this returns the hierarchy-tree pointer whose leading
        // fixed string is the hierarchy identity. Aggregate wrappers may
        // rename the visible RenderObj through +0x14 without changing this
        // hierarchy identity. The three authored ANIM/SUFFIX branches are:
        //   ANIM empty      -> "%s.%s%s" (Hierarchy, Hierarchy, SUFFIX)
        //   ANIM no '.'     -> "%s.%s"   (Hierarchy, ANIM)
        //   ANIM has '.'    -> ANIM verbatim
        if (animation.Length == 0)
            return $"{hierarchyIdentity}.{hierarchyIdentity}{suffix}";
        if (animation.Contains('.'))
            return animation;
        return $"{hierarchyIdentity}.{animation}";
    }

    // Animation clone004598B0 ->004597E0 ->00457BE0 copies flags &0x6051F.
    // STARTS_ENABLED is separately inspected on the template by0045FFB0.
    // Packet mutations act on the copied runtime state, never on this template.
    public static EnbRslStateFlags CopyTemplateFlags(EnbRslStateFlags flags)
        => flags with {ActivateNextOnce=false,StopOnce=false,RemovePrototype=false,
            Stationary=false,StartsEnabled=false};

    public static uint EncodeAuthoredFlags(EnbRslStateFlags flags)
    {
        ArgumentNullException.ThrowIfNull(flags);
        uint value = 0;
        if (flags.Delete) value |= 0x0000_0001u;
        if (flags.Deactivate) value |= 0x0000_0002u;
        if (flags.Stop) value |= 0x0000_0004u;
        if (flags.ActivateNext) value |= 0x0000_0008u;
        if (flags.Enabled) value |= 0x0000_0010u;
        if (flags.ActivateNextOnce) value |= 0x0000_0020u;
        if (flags.StopOnce) value |= 0x0000_0040u;
        if (flags.Precached) value |= 0x0000_0100u;
        if (flags.InstanceData) value |= 0x0000_0400u;
        if (flags.RemovePrototype) value |= 0x0000_0800u;
        if (flags.Stationary) value |= 0x0000_4000u;
        if (flags.StartsEnabled) value |= 0x0001_0000u;
        if (flags.DontStopSound) value |= 0x0002_0000u;
        if (flags.DontLoopSound) value |= 0x0004_0000u;
        return value;
    }

    public static bool RequiresLocalTransitionStateMachine(EnbRslStateFlags flags)
    {
        ArgumentNullException.ThrowIfNull(flags);
        return flags.Delete ||
               flags.Deactivate ||
               flags.ActivateNext ||
               flags.ActivateNextOnce;
    }

    /// <summary>
    /// Computes the frame current retail would hold for the bounded non-local-
    /// transition cases. Client time is a millisecond counter, so uint
    /// subtraction intentionally preserves normal wraparound behavior.
    /// </summary>
    public static bool TryComputeFrame(
        EnbW3dRenderHierarchyAnimation animation,
        EnbRslStateFlags flags,
        uint activationClientTime,
        uint currentClientTime,
        out float frame,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(flags);
        frame = 0f;
        diagnostic = null;

        if (RequiresLocalTransitionStateMachine(flags))
        {
            diagnostic =
                $"RSL animation '{animation.Name}' carries DELETE/DEACTIVATE/ACTIVATE_NEXT transition semantics; local retail RSL transition execution is deliberately not approximated.";
            return false;
        }

        if (animation.NumFrames == 0)
        {
            diagnostic = $"RSL animation '{animation.Name}' contains zero frames.";
            return false;
        }
        if (animation.PlaybackFrameRate == 0)
        {
            diagnostic = $"RSL animation '{animation.Name}' has zero frame rate.";
            return false;
        }

        uint elapsedMilliseconds = unchecked(currentClientTime - activationClientTime);
        double rawFrame = animation.PlaybackFrameRate * (double)elapsedMilliseconds / 1000.0;
        if (!double.IsFinite(rawFrame) || rawFrame < 0.0)
        {
            diagnostic = $"RSL animation '{animation.Name}' produced invalid elapsed frame {rawFrame}.";
            return false;
        }

        double terminalFrame = animation.NumFrames - 1.0;
        double resolved;
        if (flags.Stop || flags.StopOnce)
        {
            // FUN_00459B10 enters its end-of-animation path at NumFrames - 1
            // and, while the state remains enabled, clamps STOP/STOP_ONCE there.
            resolved = Math.Min(rawFrame, terminalFrame);
        }
        else if (rawFrame < terminalFrame)
        {
            resolved = rawFrame;
        }
        else
        {
            // After the same terminal callback boundary, an enabled ordinary
            // looping state stores the CRT floating remainder using NumFrames.
            resolved = rawFrame % animation.NumFrames;
        }

        frame = (float)resolved;
        // A remainder below NumFrames can round up when stored as a float.
        // Normalize that loop endpoint instead of rejecting a valid animation.
        if (!flags.Stop && !flags.StopOnce && frame == animation.NumFrames)
            frame = 0f;
        if (!float.IsFinite(frame) || frame < 0f || frame >= animation.NumFrames)
        {
            diagnostic =
                $"RSL animation '{animation.Name}' resolved frame {frame} outside [0,{animation.NumFrames}).";
            frame = 0f;
            return false;
        }
        return true;
    }
}
