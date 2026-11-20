using EnbNext.Presentation;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

public sealed partial class EnbNormalCameraController
{
    private EnbSceneCameraModeSnapshot? _adjustedMode;
    private readonly Dictionary<int, EnbSceneCameraModeSnapshot> _savedModes = [];
    private bool _manualCameraInput;

    //50ABC0 retains controller+28 across frames; ordinary mouse movement
    //clears it at50B0E9. Preset changes do not reset this input state.
    public void EndManualCameraInput() => _manualCameraInput = false;

    public bool ZoomCameraWheel(EnbSceneCameraStateSnapshot state, bool forward)
    {
        if (IsFirstPerson || GetCurrentMode(state) is not {} mode) return false;
        _adjustedMode = mode with { Distance = ResolveWheelDistance(mode.Distance, forward) };
        _manualCameraInput = true;
        return true;
    }

    //50AE28..50AE7C compares the retained product against the lower limit,
    //then the stored float against the upper limit. The exponent FST is unused.
    public static float ResolveWheelDistance(float distance, bool forward,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        return ResolveExponentialDistance(distance,forward?-.5f:.5f);
    }

    private static float ResolveExponentialDistance(float distance,float exponent)
    {
        R product=R.NaturalExponent((R)exponent)*(R)distance;
        float stored=(float)product;
        if((R.CompareFloatMemory(product,0x3f000000)&0x100)!=0)return .5f;
        return stored>10?10:stored;
    }

    public bool BeginManualCameraInput(EnbSceneCameraStateSnapshot state,bool distance)
    {
        if(IsFirstPerson || GetCurrentMode(state) is not {} mode)return false;
        _manualCameraInput=true;
        _adjustedMode=ResolveDragMode(mode,.5f,.5f,0,distance,initialGesture:true);
        return true;
    }

    public static EnbSceneCameraModeSnapshot ResolveDragMode(EnbSceneCameraModeSnapshot mode,
        float x,float y,double elapsedMilliseconds,bool distance,bool initialGesture=false,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        //50ACC1..50ACD1 stores the frame multiplier before either branch.
        float scale=(float)((R)elapsedMilliseconds*(R).1f);
        if(distance)
        {
            //50AF03..50AF11 spills the exponent before FLDL2E/F2XM1.
            float exponent=initialGesture?0:(float)(((R)y-(R).5f)*(R)scale);
            return mode with {Distance=ResolveExponentialDistance(mode.Distance,exponent)};
        }
        if(initialGesture)return mode; //50AFAB..50B008 bypasses tilt clamping.
        float heading=(float)(((R).5f-(R)x)*(R)scale+(R)mode.HeadingRadians);
        R tilt=((R)y-(R).5f)*(R)scale+(R)mode.TiltRadians;
        float stored=(float)tilt;
        if((R.CompareFloatMemory(tilt,0xbfbde44f)&0x100)!=0)stored=BitConverter.UInt32BitsToSingle(0xbfbde44f);
        else if(stored>BitConverter.UInt32BitsToSingle(0x3fbde44f))stored=BitConverter.UInt32BitsToSingle(0x3fbde44f);
        return mode with {HeadingRadians=heading,TiltRadians=stored};
    }

    public EnbSceneCameraModeSnapshot? GetCurrentMode(EnbSceneCameraStateSnapshot state)
        => _adjustedMode ?? _savedModes.GetValueOrDefault(_activeModeIndex) ??
           state.Modes.FirstOrDefault(m => m.Index == _activeModeIndex);

    public void RestoreCamera() => _adjustedMode = null;

    public void RememberSavedMode(EnbSceneCameraModeSnapshot mode)
    {
        _savedModes[mode.Index] = mode;
        _adjustedMode = null;
    }

    // 0050ABC0: normalized mouse displacement, frame delta * .1, exponential
    // distance in [.5,10], heading subtracts X, tilt adds Y in [-85,+85] degrees.
    // Shift wins over Control; Alt suppresses both. First-person bridge movement
    // belongs to its own renderer and is intentionally not a third-person orbit.
    public bool AdjustCamera(EnbSceneCameraStateSnapshot state, double dx, double dy,
        double elapsedMilliseconds, bool distance)
    {
        if (IsFirstPerson || GetCurrentMode(state) is not {} mode ||
            !double.IsFinite(dx) || !double.IsFinite(dy) ||
            !double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < 0) return false;
        _manualCameraInput = true;
        _adjustedMode = ResolveDragMode(mode,(float)(.5+dx),(float)(.5+dy),elapsedMilliseconds,distance);
        return true;
    }

    private EnbSceneCameraStateSnapshot WithCameraAdjustments(EnbSceneCameraStateSnapshot state)
        => GetCurrentMode(state) is {} mode
            ? state with { Modes = [..state.Modes.Select(m => m.Index == mode.Index ? mode : m)] }
            : state;
}
