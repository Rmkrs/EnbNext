namespace EnbNext.Rendering;

/// <summary>Object/Bone +30 (0045ECD0), including terminal004584B0 ordering.
/// Inputs are retained instance fields, not freshly parsed authored flags.</summary>
public static class EnbCurrentRetailRslObjectLifetimeRuntime
{
    public static EnbCurrentRetailRslObjectLifetimeUpdate Update(
        float elapsed, float duration, uint flags, float deltaSeconds, bool hasNext)
    {
        //0045ECDA spills the sum before comparing it with duration.
        float accumulated = (float)((double)deltaSeconds + elapsed);
        if ((flags & 7) == 0 || !(accumulated > duration))
            return new(accumulated, flags, false, false, false, false, 0);

        float leftover = (float)((double)accumulated - duration);
        if ((flags & 0x6c) != 0) flags |= 0x80;
        bool deactivate = (flags & 0x2a) != 0;
        if (deactivate) flags &= ~0x10u;
        bool delete = (flags & 1) != 0;
        bool activateNext = (flags & 0x28) != 0;
        if (activateNext) flags &= ~0x20u;
        return new(accumulated, flags, true, deactivate, delete,
            activateNext && hasNext, leftover);
    }
}

public readonly record struct EnbCurrentRetailRslObjectLifetimeUpdate(
    float ElapsedSeconds,
    uint Flags,
    bool TerminalCallback,
    bool DeactivateLinkChain,
    bool DeleteOwner,
    bool ActivateNext,
    float LeftoverSeconds);
