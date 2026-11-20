using System.Numerics;
using EnbNext.Presentation;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>
/// Retail-equivalent normal in-space camera-mode selector.
///
/// Current retail client.exe initializes the remembered/default third-person
/// slot to mode 1, reserves mode 0 for first person, restores the remembered
/// non-zero slot when returning to third person, and cycles only modes 1..N-1.
/// This controller owns only that local presentation state. It never reaches
/// back into mutable Net7/domain state.
/// </summary>
public sealed partial class EnbNormalCameraController
{
    // Current MainView constructs its world camera at a fixed 4:3 projection
    // aspect. The physical render surface remains separate and may be wide.
    public const double CurrentRetailGameplayProjectionAspectRatio = 4.0 / 3.0;

    public const int FirstPersonModeIndex = 0;
    public const int DefaultThirdPersonModeIndex = 1;

    // Reopens: provider +0x14 returns normalized angular rate, not
    // world velocity. 006F3ED0 consumes Z; 0079BFC0 quarters the bank target.
    public const byte ActivePlayerObjectType = 1;
    public const float RetailBankAngularRateScale = -500f;
    public const float RetailBankClampRadians = 0.62831855f;
    public const float RetailBankTargetMultiplier = 0.25f;
    public const float RetailBankSlewRateRadiansPerMillisecond = 0.001f;
    public const float RetailBankTargetEpsilonRadians = 0.0001f;

    private int _activeModeIndex;
    private int _rememberedThirdPersonModeIndex;
    private float _currentBankRadians;
    private float _targetBankRadians;
    private Vector3 _currentPosition;
    private Quaternion _currentOrientation;
    private Quaternion? _currentRetailOrientation;
    private bool _hasPose;
    private ulong? _poseAnchorIdentity;

    public void ResetMotion()
    {
        _hasPose = false;
        _currentRetailOrientation = null;
        _currentBankRadians = _targetBankRadians = 0;
    }

    public EnbNormalCameraController(bool startInFirstPerson = false)
    {
        _rememberedThirdPersonModeIndex = DefaultThirdPersonModeIndex;
        _activeModeIndex = startInFirstPerson
            ? FirstPersonModeIndex
            : DefaultThirdPersonModeIndex;
    }

    public int ActiveModeIndex => _activeModeIndex;
    public int RememberedThirdPersonModeIndex => _rememberedThirdPersonModeIndex;
    public bool IsFirstPerson => _activeModeIndex == FirstPersonModeIndex;
    public float CurrentBankRadians => _currentBankRadians;
    public float TargetBankRadians => _targetBankRadians;
    public void ApplyCollisionPosition(Vector3 position)
    {
        if (_hasPose && float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z))
            _currentPosition = position;
    }

    /// <summary>
    /// Creates the normal-camera selector from retail's session-start camera
    /// preference carried across the immutable presentation boundary.
    /// </summary>
    public static EnbNormalCameraController FromRetailStartup(EnbSceneCameraStateSnapshot cameraState)
    {
        ArgumentNullException.ThrowIfNull(cameraState);
        return new EnbNormalCameraController(cameraState.StartInFirstPerson);
    }

    public bool SelectFirstPerson(EnbSceneCameraStateSnapshot cameraState)
    {
        ArgumentNullException.ThrowIfNull(cameraState);
        if (!ContainsMode(cameraState, FirstPersonModeIndex))
            return false;

        _activeModeIndex = FirstPersonModeIndex;
        _adjustedMode = null;
        return true;
    }

    public bool RestoreDefaultThirdPerson(EnbSceneCameraStateSnapshot cameraState)
    {
        ArgumentNullException.ThrowIfNull(cameraState);
        if (!ContainsMode(cameraState, _rememberedThirdPersonModeIndex))
            return false;

        _activeModeIndex = _rememberedThirdPersonModeIndex;
        _adjustedMode = null;
        return true;
    }

    public bool SelectThirdPersonMode(EnbSceneCameraStateSnapshot cameraState, int modeIndex)
    {
        ArgumentNullException.ThrowIfNull(cameraState);
        if (modeIndex <= FirstPersonModeIndex || !ContainsMode(cameraState, modeIndex))
            return false;

        _activeModeIndex = modeIndex;
        _adjustedMode = null;
        _rememberedThirdPersonModeIndex = modeIndex;
        return true;
    }

    public bool SelectNextThirdPersonMode(EnbSceneCameraStateSnapshot cameraState)
    {
        ArgumentNullException.ThrowIfNull(cameraState);
        if (IsFirstPerson || !TryGetSequentialModeCount(cameraState, out int modeCount) || modeCount <= 1)
            return false;

        int next = _activeModeIndex + 1;
        if (next >= modeCount)
            next = DefaultThirdPersonModeIndex;

        _activeModeIndex = next;
        _adjustedMode = null;
        _rememberedThirdPersonModeIndex = next;
        return true;
    }

    public bool SelectPreviousThirdPersonMode(EnbSceneCameraStateSnapshot cameraState)
    {
        ArgumentNullException.ThrowIfNull(cameraState);
        if (IsFirstPerson || !TryGetSequentialModeCount(cameraState, out int modeCount) || modeCount <= 1)
            return false;

        int previous = _activeModeIndex < 2
            ? modeCount - 1
            : _activeModeIndex - 1;

        _activeModeIndex = previous;
        _adjustedMode = null;
        _rememberedThirdPersonModeIndex = previous;
        return true;
    }

    public bool TryResolveFrame(
        EnbSceneCameraStateSnapshot cameraState,
        IEnbRenderTransform anchorTransform,
        byte anchorObjectType,
        double elapsedMilliseconds,
        EnbRenderViewport viewport,
        out EnbNormalCameraFrame frame,
        EnbCameraPoseOverride? temporaryCamera = null)
    {
        ArgumentNullException.ThrowIfNull(cameraState);
        ArgumentNullException.ThrowIfNull(anchorTransform);
        frame = default;
        cameraState = WithCameraAdjustments(cameraState);

        //743E77..743F01 changes the retained normal-mode fields, not its
        //lifetime.7945E0 continues smoothing third-person presets and snaps
        //bridge mode; neither branch resets the normal mode's bank smoother.
        if (_hasPose && _poseAnchorIdentity != cameraState.AnchorRenderIdentity) ResetMotion();

        if (!double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < 0 ||
            !EnbRenderCameraIntentResolver.TryResolveModeGeometry(
                cameraState,
                _activeModeIndex,
                out EnbChaseCameraModeGeometry mode) ||
            !EnbRenderCameraIntentResolver.TryResolveNormalClipRange(cameraState, out EnbRenderClipRange clipRange) ||
            !TryUpdateDynamicBank(anchorTransform, anchorObjectType, elapsedMilliseconds) ||
            !TryResolveNativeNormalPose(anchorTransform, mode, _currentBankRadians, out Vector3 position, out Quaternion normalNative))
        {
            return false;
        }

        Quaternion orientation=EnbRetailPointCameraOrientation.CanonicalOrientation(normalNative);
        if (temporaryCamera?.Position is {} temporaryPosition) position = temporaryPosition;
        if (temporaryCamera?.Orientation is {} temporaryOrientation) orientation = temporaryOrientation;
        Quaternion previousOrientation=_currentRetailOrientation ?? EnbRetailPointCameraOrientation.FromCanonicalOrientation(_currentOrientation);
        orientation=temporaryCamera?.RetailCameraOrientation ?? (temporaryCamera?.Orientation is not null
            ? EnbRetailPointCameraOrientation.FromCanonicalOrientation(orientation) : normalNative);
        // 007945E0 / 00799E40 / 00799610: normal third person follows a
        // desired pose. First frame, first person and large jumps snap.
        bool discontinuity = _hasPose && EnbRetailCameraSmoothing.IsDiscontinuity(_currentPosition,position);
        bool firstPerson = IsFirstPerson && temporaryCamera?.ForceThirdPerson != true;
        if (_hasPose && (!firstPerson || temporaryCamera?.ForceSmooth == true) && !discontinuity && temporaryCamera?.Snap != true)
        {
            (position,orientation)=EnbRetailCameraSmoothing.Update(_currentPosition,previousOrientation,
                position,orientation,elapsedMilliseconds,_manualCameraInput);
        }
        Quaternion retainedNative=orientation;
        orientation=EnbRetailPointCameraOrientation.CanonicalOrientation(orientation);
        if (!EnbResolvedRenderCamera.TryCreate(
                position, orientation,
                viewport,
                mode.HorizontalFieldOfViewRadians,
                CurrentRetailGameplayProjectionAspectRatio,
                clipRange,
                out EnbResolvedRenderCamera camera))
        {
            return false;
        }

        _currentPosition = position;
        _currentOrientation = orientation;
        _currentRetailOrientation = retainedNative;
        camera=camera with {Basis=EnbRetailPointCameraOrientation.CameraBasis(retainedNative)};
        _poseAnchorIdentity = cameraState.AnchorRenderIdentity;
        _hasPose = true;

        frame = new EnbNormalCameraFrame(
            _activeModeIndex,
            _rememberedThirdPersonModeIndex,
            firstPerson,
            _currentBankRadians,
            _targetBankRadians,
            mode,
            camera);
        return true;
    }

    /// <summary>
    /// Resolves the current-retail normal-player bank target from the sampled
    /// angular rate carried through the immutable presentation snapshot.
    /// Raw provider-slot trace supersedes the velocity claim. Rotation.Z
    /// is already normalized per millisecond; there is no extra factor of 100.
    /// </summary>
    public static bool TryResolveNormalPlayerBankTarget(
        IEnbRenderTransform anchorTransform,
        byte anchorObjectType,
        out float targetBankRadians)
    {
        ArgumentNullException.ThrowIfNull(anchorTransform);
        targetBankRadians = 0f;

        if (!IsFinite(anchorTransform.Rotation))
            return false;

        // Closes only retail's normal active-player branch. A non-player
        // anchor receives no guessed alternate bank term; its static camera
        // pose remains usable until the special-camera branch is implemented.
        if (anchorObjectType != ActivePlayerObjectType)
            return true;

        double rawBank =
            anchorTransform.Rotation.Z * RetailBankAngularRateScale;
        if (!double.IsFinite(rawBank))
            return false;

        double clamped = Math.Clamp(
            rawBank,
            -RetailBankClampRadians,
            RetailBankClampRadians);
        double target = clamped * RetailBankTargetMultiplier;
        if (!double.IsFinite(target))
            return false;

        targetBankRadians = (float)target;
        return true;
    }

    /// <summary>
    /// Advances retail's stateful bank smoother by presentation-frame elapsed
    /// time. The smoother is linear, not exponential: it moves by exactly
    /// 0.001 radians per elapsed millisecond and clamps onto the target on
    /// overshoot. 498CC0 leaves tiny residuals alone unless the target is zero.
    /// </summary>
    private bool TryUpdateDynamicBank(
        IEnbRenderTransform anchorTransform,
        byte anchorObjectType,
        double elapsedMilliseconds)
    {
        if (!TryResolveNormalPlayerBankTarget(anchorTransform, anchorObjectType, out float target) ||
            !double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < 0)
        {
            return false;
        }

        _targetBankRadians = target;
        using var scope=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24,true);
        float difference=(float)((R)_currentBankRadians-target);
        if (MathF.Abs(difference) <= RetailBankTargetEpsilonRadians)
        {
            if(target==0)_currentBankRadians=0;
            return true;
        }
        //498E00 retains the product through the add/subtract and compares the
        //result before selecting the exact target on crossing.
        R step=(R)(float)elapsedMilliseconds*RetailBankSlewRateRadiansPerMillisecond;
        bool rising=_currentBankRadians<target;
        R next=rising?(R)_currentBankRadians+step:(R)_currentBankRadians-step;
        _currentBankRadians=(float)next;
        int comparison=R.CompareFloatMemory(next,BitConverter.SingleToUInt32Bits(target));
        if(rising?(comparison&0x100)==0:(comparison&0x4100)!=0)_currentBankRadians=target;
        return float.IsFinite(_currentBankRadians);
    }

    /// <summary>79BFC0 consumes the physical provider matrix without normalizing
    /// or rebuilding it from quaternion metadata. Public orientation uses the
    /// canonical basis; production retains the native quaternion separately.</summary>
    public static bool TryResolveNormalPose(IEnbRenderTransform anchorTransform,
        EnbChaseCameraModeGeometry mode,float bankRadians,out Vector3 cameraPosition,out Quaternion cameraOrientation)
    {
        bool valid=TryResolveNativeNormalPose(anchorTransform,mode,bankRadians,out cameraPosition,out var native);
        cameraOrientation=valid?EnbRetailPointCameraOrientation.CanonicalOrientation(native):default;
        return valid;
    }

    private static bool TryResolveNativeNormalPose(IEnbRenderTransform anchorTransform,
        EnbChaseCameraModeGeometry mode,float bankRadians,out Vector3 cameraPosition,out Quaternion cameraOrientation)
    {
        ArgumentNullException.ThrowIfNull(anchorTransform);
        cameraPosition=default;cameraOrientation=default;
        if(!IsFinite(anchorTransform.Position)||!IsFinite(anchorTransform.Orientation)||!IsFinite(bankRadians)||
            !IsFinite(mode.HeadingRadians)||!IsFinite(mode.ScaledDistance)||!IsFinite(mode.TiltRadians)||!IsFinite(mode.ElevationOffsetRadians))return false;
        var matrix=anchorTransform.ProviderTransform??EnbRetailCameraQuaternionBasis.Create(anchorTransform.Orientation);
        if(anchorTransform.ProviderTransform is null)matrix.Translation=anchorTransform.Position;
        var result=EnbRetailNormalCameraPose.Evaluate(matrix,bankRadians,mode.HeadingRadians,
            mode.ScaledDistance,mode.TiltRadians,mode.ElevationOffsetRadians,scale:1);
        if(!IsFinite(result.Position)||!IsFinite(result.Orientation))return false;
        cameraPosition=result.Position;cameraOrientation=result.Orientation;
        return true;
    }

    /// <summary>
    /// Compatibility helper for the zero-bank static pose. Tests and
    /// calibration fixtures use it to keep the static composition independently
    /// pinned while the runtime controller layers bank state on top.
    /// </summary>
    public static bool TryResolveStaticNormalPose(
        IEnbRenderTransform anchorTransform,
        EnbChaseCameraModeGeometry mode,
        out Vector3 cameraPosition,
        out Quaternion cameraOrientation)
        => TryResolveNormalPose(anchorTransform, mode, 0f, out cameraPosition, out cameraOrientation);

    private static bool ContainsMode(EnbSceneCameraStateSnapshot cameraState, int modeIndex)
        => cameraState.Modes.Any(mode => mode.Index == modeIndex);

    private static bool TryGetSequentialModeCount(EnbSceneCameraStateSnapshot cameraState, out int modeCount)
    {
        modeCount = cameraState.Modes.Length;
        if (modeCount == 0)
            return false;

        for (int index = 0; index < modeCount; index++)
        {
            if (cameraState.Modes[index].Index != index)
                return false;
        }

        return true;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) &&
           float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(float value) => float.IsFinite(value);
}

public readonly record struct EnbNormalCameraFrame(
    int ActiveModeIndex,
    int RememberedThirdPersonModeIndex,
    bool IsFirstPerson,
    float BankRadians,
    float BankTargetRadians,
    EnbChaseCameraModeGeometry Mode,
    EnbResolvedRenderCamera Camera);
