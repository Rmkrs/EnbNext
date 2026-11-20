namespace EnbNext.Assets;

/// <summary>
/// Current-retail RSL Sound prototype/instance lifecycle recovered from
/// client.exe. The numeric bits are executable semantics owned by the retail
/// binary, not authored rs.ini values.
/// </summary>
public static class EnbCurrentRetailRslSoundRuntime
{
    public const uint PrototypeOnlyFlag = 0x0000_8000;
    public const uint ClonePreservedFlagMask = 0x0006_051F;

    private const uint DeleteFlag = 0x0000_0001;
    private const uint DeactivateFlag = 0x0000_0002;
    private const uint StopFlag = 0x0000_0004;
    private const uint ActivateNextFlag = 0x0000_0008;
    private const uint EnabledFlag = 0x0000_0010;
    private const uint ActivateNextOnceFlag = 0x0000_0020;
    private const uint StopOnceFlag = 0x0000_0040;
    private const uint PrecachedFlag = 0x0000_0100;
    private const uint InstanceDataFlag = 0x0000_0400;
    private const uint RemovePrototypeFlag = 0x0000_0800;
    private const uint StationaryFlag = 0x0000_4000;
    private const uint StartsEnabledFlag = 0x0001_0000;
    private const uint DontStopSoundFlag = 0x0002_0000;
    private const uint DontLoopSoundFlag = 0x0004_0000;

    public static EnbCurrentRetailRslSoundContract Describe(EnbRslStateFlags authoredFlags)
    {
        ArgumentNullException.ThrowIfNull(authoredFlags);

        uint parsedFlags = BuildParsedFlagMask(authoredFlags);
        uint prototypeFlags = parsedFlags | PrototypeOnlyFlag;
        uint instanceFlags = prototypeFlags & ClonePreservedFlagMask;

        // FUN_0045fb40 feeds the same handle method that the sound-definition
        // constructor uses for SoundLoopCount. Runtime RSL state overrides it
        // with one pass when either ACTIVATE_NEXT or DONT_LOOP_SOUND survives,
        // otherwise with zero. We keep the numeric control rather than assigning
        // a stronger infinite/finite label than the client evidence requires.
        int handleLoopCountOverride =
            (instanceFlags & (ActivateNextFlag | DontLoopSoundFlag)) != 0 ? 1 : 0;

        return new EnbCurrentRetailRslSoundContract(
            parsedFlags,
            prototypeFlags,
            instanceFlags,
            PrototypeHandleBlocked: (prototypeFlags & PrototypeOnlyFlag) != 0,
            InstanceHandleEligible: (instanceFlags & PrototypeOnlyFlag) == 0,
            HandleLoopCountOverride: handleLoopCountOverride,
            StopsHandleOnCleanup: (instanceFlags & DontStopSoundFlag) == 0,
            // The live Sound update checks bit 14 before refreshing its 3D
            // position, but the current clone mask strips authored RS_STATIONARY
            // before that update runs. Preserve that current-client quirk rather
            // than carrying the prototype-only authored bit into the instance.
            FollowsLiveObjectTransform: (instanceFlags & StationaryFlag) == 0);
    }

    public static uint BuildParsedFlagMask(EnbRslStateFlags flags)
    {
        ArgumentNullException.ThrowIfNull(flags);

        uint value = 0;
        if (flags.Delete) value |= DeleteFlag;
        if (flags.Deactivate) value |= DeactivateFlag;
        if (flags.Stop) value |= StopFlag;
        if (flags.ActivateNext) value |= ActivateNextFlag;
        if (flags.Enabled) value |= EnabledFlag;
        if (flags.RemovePrototype) value |= RemovePrototypeFlag;
        if (flags.Precached) value |= PrecachedFlag;
        if (flags.InstanceData) value |= InstanceDataFlag;
        if (flags.ActivateNextOnce) value |= ActivateNextOnceFlag;
        if (flags.StopOnce) value |= StopOnceFlag;
        if (flags.Stationary) value |= StationaryFlag;
        if (flags.StartsEnabled) value |= StartsEnabledFlag;
        if (flags.DontStopSound) value |= DontStopSoundFlag;
        if (flags.DontLoopSound) value |= DontLoopSoundFlag;
        return value;
    }
}

public sealed record EnbCurrentRetailRslSoundContract(
    uint ParsedFlags,
    uint PrototypeFlags,
    uint InstanceFlags,
    bool PrototypeHandleBlocked,
    bool InstanceHandleEligible,
    int HandleLoopCountOverride,
    bool StopsHandleOnCleanup,
    bool FollowsLiveObjectTransform);
