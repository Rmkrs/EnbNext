using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>
/// Current-retail engine-exhaust child visibility semantics.
///
/// InitializeShipVisualEffects @ 0x006FCB70 first calls the recursive helper
/// at 0x00810750 with active=false. That helper walks the complete RenderObject
/// tree and hides every child attached to an "exhaust" prefix or the five
/// character eng-digit-any pattern. Empty eng bones acquire p-eng1..4.
/// UpdateEngineThrustEffects
/// @ 0x006FDBD0 later re-enables those children for EngineThrustState == 1,
/// cached warp state == 2, or a retail emote/camera-transition override.
///
/// Ship emote playback supplies the loop/dive override. The emote camera itself
/// uses the false override virtual at 0x00792410.
/// The frame owner supplies SpaceObject+0xF8 at the engine-consumer stage.
/// </summary>
public static class EnbCurrentRetailShipExhaustVisibilityRuntime
{
    private const string ExhaustPrefix = "exhaust";

    //810750 checks length5 and digit at index3; index4 is unrestricted.
    public static bool IsEngineBoneName(string? boneName)
        =>boneName is {Length:5} && boneName.StartsWith("eng",StringComparison.OrdinalIgnoreCase)
            && char.IsAsciiDigit(boneName[3]);

    public static bool IsExhaustBoneName(string? boneName)
        => boneName is not null &&
           boneName.Length >= ExhaustPrefix.Length &&
           boneName.AsSpan(0, ExhaustPrefix.Length).Equals(
               ExhaustPrefix.AsSpan(),
               StringComparison.OrdinalIgnoreCase);

    public static EnbCurrentRetailShipExhaustVisibility Resolve(
        IEnbRenderObject obj,
        bool retailEmoteOrCameraOverrideActive = false)
    {
        ArgumentNullException.ThrowIfNull(obj);

        // 006FCB70, 006FEAC0 and 007008B0 pass the RenderObject to 00810750(false)
        // only for +0x94 types 0, 1 and 0x19; no other space-object path toggles
        // exhaust children, so another type keeps them as constructed (shown).
        if (obj.ObjectType is not (0 or 1 or 25))
        {
            return new EnbCurrentRetailShipExhaustVisibility(
                EnbCurrentRetailShipExhaustVisibilityDecision.Visible,
                EnbCurrentRetailShipExhaustVisibilityReason.NotToggled);
        }

        // 006FDBD0 returns before re-showing them when IsOrganic is materialized
        // true, so the initial hide stands.
        EnbSceneShipPresentationSnapshot? presentation = obj.ShipPresentation;
        if (presentation?.IsOrganic == true)
        {
            return new EnbCurrentRetailShipExhaustVisibility(
                EnbCurrentRetailShipExhaustVisibilityDecision.Hidden,
                EnbCurrentRetailShipExhaustVisibilityReason.InitializedHidden);
        }

        // 006FDBD0 tests the cached warp state +0xF8 only for types 0 and 1.
        return Resolve(
            presentation?.EngineThrustState,
            obj.ObjectType is 0 or 1 ? presentation?.EngineWarpState ?? presentation?.CachedWarpState ?? 0 : 0,
            retailEmoteOrCameraOverrideActive || obj.ShipEmote?.Emote is 1 or 2);
    }

    public static EnbCurrentRetailShipExhaustVisibility Resolve(
        int? engineThrustState,
        int cachedWarpState,
        bool retailEmoteOrCameraOverrideActive = false)
    {

        // Retail returns before evaluating warp/emote/camera activation while
        // EngineThrustState is unmaterialized. Initialization already hid the
        // exhaust children, so this is an exact hidden state.
        if (engineThrustState is null)
        {
            return new EnbCurrentRetailShipExhaustVisibility(
                EnbCurrentRetailShipExhaustVisibilityDecision.Hidden,
                EnbCurrentRetailShipExhaustVisibilityReason.InitializedHidden);
        }

        if (engineThrustState == 1)
        {
            return new EnbCurrentRetailShipExhaustVisibility(
                EnbCurrentRetailShipExhaustVisibilityDecision.Visible,
                EnbCurrentRetailShipExhaustVisibilityReason.EngineThrust);
        }

        if (retailEmoteOrCameraOverrideActive)
        {
            return new EnbCurrentRetailShipExhaustVisibility(
                EnbCurrentRetailShipExhaustVisibilityDecision.Visible,
                EnbCurrentRetailShipExhaustVisibilityReason.EmoteOrCameraOverride);
        }

        if (cachedWarpState == 2)
        {
            return new EnbCurrentRetailShipExhaustVisibility(
                EnbCurrentRetailShipExhaustVisibilityDecision.Visible,
                EnbCurrentRetailShipExhaustVisibilityReason.WarpEnter);
        }

        return new EnbCurrentRetailShipExhaustVisibility(
            EnbCurrentRetailShipExhaustVisibilityDecision.Hidden,
            EnbCurrentRetailShipExhaustVisibilityReason.Inactive);
    }
}

public enum EnbCurrentRetailShipExhaustVisibilityDecision
{
    Hidden,
    Visible,
}

public enum EnbCurrentRetailShipExhaustVisibilityReason
{
    InitializedHidden,
    Inactive,
    EngineThrust,
    WarpEnter,
    EmoteOrCameraOverride,
    NotToggled,
}

public readonly record struct EnbCurrentRetailShipExhaustVisibility(
    EnbCurrentRetailShipExhaustVisibilityDecision Decision,
    EnbCurrentRetailShipExhaustVisibilityReason Reason)
{
    public bool ShouldDraw => Decision is not EnbCurrentRetailShipExhaustVisibilityDecision.Hidden;
}
