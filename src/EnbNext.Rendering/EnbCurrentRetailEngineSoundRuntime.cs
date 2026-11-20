using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Ordinary ship engine voices from UpdateEngineThrustEffects @ 006FDBD0.</summary>
public static class EnbCurrentRetailEngineSoundRuntime
{
    public static string? ResolveName(IEnbRenderObject obj, EnbRetailSoundCatalog sounds)
    {
        if (obj.ObjectType is not (0 or 1 or 25) || obj.ShipPresentation is not { IsOrganic: not true, EngineTrailType: int trail })
            return null;
        // 006F33F0 stores the model's base asset at +A8; 006F39F0 uses it
        // for cbasset.ini BASE-%d. 006FDD32 uses its signed remainder, no +1.
        string name = obj.ObjectType == 1
            ? FormattableString.Invariant($"engine_loop_{unchecked(trail + 1)}")
            : FormattableString.Invariant($"npc_engine_loop_{obj.BaseAsset % 6}");
        return sounds.TryGetDefinition(name, out _) ? name : "engine_loop_1";
    }

    // 006FDFC5..006FDFEC suppresses the ordinary voice for charge/travel (1/2),
    // not for retained exit/failure values. In particular global 3 may remain
    // after leaving warp until the next trip; requiring zero silences thrust.
    // Both consumers read the ordered +F8 cache, not the retained properties.
    public static bool ShouldPlay(EnbSceneShipPresentationSnapshot ship)
        => ship.EngineThrustState == 1 && (ship.EngineWarpState ?? ship.CachedWarpState) is not (1 or 2);

    // 006FDBD0 applies the loop/dive override before the engine voice gate,
    // but returns before it if EngineThrustState has not materialized.
    public static bool ShouldPlay(IEnbRenderObject obj)
        => obj.ShipPresentation is { EngineThrustState: not null } ship &&
           (ship.EngineThrustState == 1 || obj.ShipEmote?.Emote is 1 or 2) &&
           (ship.EngineWarpState ?? ship.CachedWarpState) is not (1 or 2);

    public static string? ValidateDefinition(EnbRetailSoundDefinition definition)
    {
        if (definition.EffectiveMinVolumeShift != 0 || definition.EffectiveMaxVolumeShift != 0 ||
            definition.EffectiveMinPitchShift != 0 || definition.EffectiveMaxPitchShift != 0)
            return "randomized continuous engine modifiers are not implemented";
        if (definition.EffectiveLoopCount != 0) return "engine sound is not a continuous loop";
        if (!float.IsFinite(definition.EffectiveDopplerTweak) ||
            !float.IsFinite(definition.EffectiveMinDopplerShift) || definition.EffectiveMinDopplerShift <= 0 ||
            !float.IsFinite(definition.EffectiveMaxDopplerShift) ||
            definition.EffectiveMaxDopplerShift < definition.EffectiveMinDopplerShift ||
            !float.IsFinite(definition.EffectivePitch * definition.EffectiveMaxDopplerShift))
            return "invalid engine Doppler limits/tweak";
        // Engine voices implement Doppler; unrelated effect consumers remain bounded.
        return EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(definition with { MinDopplerShift = 1, MaxDopplerShift = 1 });
    }
}
