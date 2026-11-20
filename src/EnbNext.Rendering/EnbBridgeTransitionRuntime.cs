using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>Retail bridge state selection (0053C670), callbacks (0053B500),
/// and non-looping UI animation execution (005DD030/005DD3C0).</summary>
public sealed class EnbBridgeTransitionRuntime
{
    private static readonly string?[] Suffixes = [null, "-ml", "-mr", "-mf", "-ml", null, "-rl", "-lf",
        "-mr", "-rl", null, "-rf", "-mf", "-lf", "-rf", null];
    private static readonly bool[] Reverse = [false, true, true, true, false, false, false, true,
        false, true, false, true, false, false, false, false];
    private static readonly string[] Cameras = ["CAM_MAIN", "CAM_LEFT", "CAM_RIGHT", "CAM_FRONT"];
    private EnbW3dRenderHierarchyAnimation? _animation;
    private float _frame;
    private bool _reverse, _firstStep;
    public int RequestedState { get; private set; }
    public int CompletedState { get; private set; }
    public string? LastFailure { get; private set; }
    public bool IsTransitioning => RequestedState != CompletedState;
    public EnbW3dAnimationSample? Sample => _animation is { } animation ? new(animation.Name, _frame) : null;
    public string CameraAttachment(bool cameraBridge) => !cameraBridge || IsTransitioning ? Cameras[0] : Cameras[RequestedState];

    public void Reset()
    {
        RequestedState = CompletedState = 0; _animation = null; _frame = 0; _firstStep = false;
        LastFailure = null;
    }

    public bool TryRequest(EnbRetailBridgeResource bridge, int state, bool animate)
    {
        if (state is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(state));
        if (IsTransitioning || state == RequestedState) return false;
        if (!animate) { RequestedState = CompletedState = state; _animation = null; LastFailure = null; return true; }
        int index = RequestedState + state * 4;
        string name = bridge.Name + Suffixes[index];
        var animation = bridge.Resource.Animations.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (animation is null || animation.NumFrames == 0 || animation.FrameRate == 0)
        {
            // Contain optional-asset failures before state mutation. Retail's failed
            // request leaves requested/completed unequal; preserving that stall is
            // not required compatibility behavior (see project failure discipline).
            LastFailure = animation is null ? null : "Invalid bridge transition timing: " + name;
            return false;
        }
        LastFailure = null;
        _animation = animation; _reverse = Reverse[index]; _firstStep = true;
        _frame = _reverse ? animation.NumFrames - 1 : 0;
        RequestedState = state;
        return true;
    }

    public void Advance(double elapsedMilliseconds,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        if (!double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));
        if (_animation is not { } animation) return;
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        // Bridge marks UI mesh bit 1 on enqueue; the first frame-change callback
        // clears it. That first update advances exactly one authored frame.
        // 5B944C..5B945B spills elapsed seconds before 5DD3C0 consumes them.
        float seconds=(float)((R)elapsedMilliseconds*(R).001f);
        R step = _firstStep ? (R)1f : (R)(float)animation.FrameRate*(R)seconds;
        _firstStep = false;
        float next = (float)(_reverse?(R)_frame-(R)step:(R)step+(R)_frame);
        bool complete = _reverse ? next < 0 : next > animation.NumFrames - 1;
        _frame = Math.Clamp(next, 0, animation.NumFrames - 1);
        if (!complete) return;
        CompletedState = RequestedState;
        _animation = null; // delete-self request detaches animation (005DD720).
    }
}
