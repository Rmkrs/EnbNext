using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>009C6270 frame evaluation over the retained hierarchy animation
/// fields. Animation rate/count remain concrete virtual consumers.</summary>
public sealed class EnbCurrentRetailHierarchyAnimationState
{
    public int Mode { get; set; }
    public float CurrentFrame { get; set; }
    public int Playback { get; set; }
    public uint PreviousTime { get; set; }
    public float PreviousFrame { get; set; }
    public bool HasHierarchy { get; set; }
    public bool HasAnimation { get; set; }
    public byte HierarchyUpdated { get; set; }
    public uint Flags { get; set; }

    /// <summary>009C5DD0 mode2 selected branch. Pose and cue consumers
    /// resolve the retained animation/hierarchy; neither is replaced here.</summary>
    public void RefreshSingle(Func<uint> time, Func<double> frameRate, Func<int> frameCount,
        Action<float> evaluatePose, Func<byte> hasCues, Func<int> cuePivot,
        Func<int, float, float, double> dispatchCues)
    {
        if (Playback != 0 && Mode == 2) AdvanceFrame(time, frameRate, frameCount);
        if (HasAnimation && HasHierarchy) evaluatePose(CurrentFrame);
        HierarchyUpdated = 1;
        if (hasCues() != 0)
        {
            int pivot = cuePivot();
            PreviousFrame = (float)dispatchCues(pivot, PreviousFrame, CurrentFrame);
        }
        HierarchyUpdated = 1;
    }

    private void AdvanceFrame(Func<uint> time, Func<double> frameRate, Func<int> frameCount)
    {
        float previous = PreviousFrame;
        PreviousFrame = CurrentFrame;
        double frame = Evaluate(time(), frameRate, frameCount);
        CurrentFrame = (float)frame;
        float compared = PreviousFrame;
        PreviousTime = time();
        if (frame == compared || double.IsNaN(frame) || float.IsNaN(compared)) PreviousFrame = previous;
        HierarchyUpdated = 0;
    }

    // A double preserves FILD's integer result until its actual float store.
    public double Evaluate(uint time, Func<double> frameRate, Func<int> frameCount)
    {
        if (Mode != 2) return 0;
        float frame = CurrentFrame;
        if (Playback == 0) return frame;
        using var precision = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        float elapsed = unchecked(time - PreviousTime);
        R rate = frameRate();
        int playback = Playback;
        frame = (float)(rate * elapsed * BitConverter.UInt32BitsToSingle(0x3a83126f) + frame);
        if (playback == 1)
        {
            int period = unchecked(frameCount() - 1);
            if (period <= (double)frame || float.IsNaN(frame))
                frame = (float)((R)frame - unchecked(frameCount() - 1));
            period = unchecked(frameCount() - 1);
            if (period <= (double)frame || float.IsNaN(frame)) return 0;
        }
        else if (playback == 2)
        {
            int period = unchecked(frameCount() - 1);
            if (period <= (double)frame || float.IsNaN(frame)) return unchecked(frameCount() - 1);
        }
        return frame;
    }
}
