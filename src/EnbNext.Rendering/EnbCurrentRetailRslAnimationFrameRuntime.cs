namespace EnbNext.Rendering;

/// <summary>Concrete Animation00459B10 frame arithmetic. The terminal callback
/// runs between Advance and Publish and may mutate enabled flags and frame.</summary>
public static class EnbCurrentRetailRslAnimationFrameRuntime
{
    public static EnbCurrentRetailRslAnimationFrameAdvance Advance(
        float oldFrame, float frameRate, uint numFrames, float deltaSeconds)
    {
        float raw = (float)((double)frameRate * deltaSeconds + oldFrame);
        float last = unchecked((int)(numFrames - 1));
        //FCOMP/TEST AH,1 takes the ordinary publication branch on unordered.
        bool terminal = raw >= last;
        float leftover = terminal
            ? (float)(deltaSeconds - ((double)unchecked((int)numFrames) - oldFrame) / frameRate)
            : 0;
        return new(raw, last, numFrames, terminal, leftover);
    }

    public static float Publish(EnbCurrentRetailRslAnimationFrameAdvance advance, uint flags)
    {
        if (!advance.TerminalCallback) return advance.RawFrame;
        if ((flags & 0x10) == 0) return 0;
        if ((flags & 0x44) != 0) return advance.LastFrame;
        //00A26E5A consumes the spilled raw frame and signed frame-count float.
        float count = unchecked((int)advance.NumFrames);
        return (float)((double)advance.RawFrame % count);
    }
}

public readonly record struct EnbCurrentRetailRslAnimationFrameAdvance(
    float RawFrame, float LastFrame, uint NumFrames, bool TerminalCallback, float LeftoverSeconds);
