namespace EnbNext.Assets;

/// <summary>
/// Current-retail RSL Sound completion semantics recovered from client.exe.
/// This deliberately separates the audio handle becoming non-live from the
/// render-state consequence applied by the Sound class.
/// </summary>
public static class EnbCurrentRetailRslSoundCompletionRuntime
{
    private const uint ActivateNextFlag = 0x0000_0008;

    public static EnbCurrentRetailRslSoundCompletionContract Describe(
        EnbRslStateDefinition state,
        EnbCurrentRetailRslSoundContract sound)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(sound);

        bool finiteOnePass = sound.HandleLoopCountOverride == 1;
        bool activateNextFlag = (sound.InstanceFlags & ActivateNextFlag) != 0;
        int? completionLinkStateId = activateNextFlag ? state.LinkStateId : null;

        return new EnbCurrentRetailRslSoundCompletionContract(
            FiniteOnePass: finiteOnePass,
            DeactivatesCurrentOnCompletion: finiteOnePass,
            EntersGenericTransitionOnCompletion: finiteOnePass && activateNextFlag,
            CompletionLinkStateId: completionLinkStateId,
            AuthoredNextIgnoredBySoundFactory: state.NextStateId.HasValue);
    }
}

public sealed record EnbCurrentRetailRslSoundCompletionContract(
    bool FiniteOnePass,
    bool DeactivatesCurrentOnCompletion,
    bool EntersGenericTransitionOnCompletion,
    int? CompletionLinkStateId,
    bool AuthoredNextIgnoredBySoundFactory);
