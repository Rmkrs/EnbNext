using System.Collections.Immutable;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI Local window actions that need the selected target's server verbs: opening a corpse's loot
/// (the Loot verb, 00030000, as its scanner button) and approaching an object (the Follow verb, 000D0000, as the
/// target panel's Follow). The object is targeted first with the ordinary target request; the verb is pressed once,
/// when the server offers it enabled for that target, exactly as a click on its button would. Nothing is sent that
/// the retail buttons would not send. When the server says the object is too far to follow, an approach first warps to
/// it as the warp button does with that target selected, and follows once in range.
/// </summary>
internal sealed partial class EnbWorldUi
{
    private const uint LootVerb = 0x00030000, FollowVerb = 0x000D0000;
    private (uint Id, bool Loot, bool Follow, bool LootSent, bool FollowSent, bool WarpSent, bool WarpSeen, long Since)? localPending;
    /// <summary>The warp button's request with the current target selected (warp to it).</summary>
    internal Func<Task>? LocalWarpRequested { get; set; }
    /// <summary>Corpses whose loot was opened (the first opening takes their credits). Next's own record: the game
    /// does not say. An id leaves the record when its corpse leaves the scene, so a later object with that id is new.</summary>
    /// <remarks>A small ring of the most recent openings is enough: corpses do not last long.</remarks>
    private readonly uint[] openedCorpses = new uint[64];
    private int openedCorpsesNext;
    private long openedCorpsesPruned;

    private bool CorpseOpened(uint id) => id != 0 && Array.IndexOf(openedCorpses, id) >= 0;

    private void TrackOpenedCorpses()
    {
        if (Inventory.LootHulkId is uint opened && !CorpseOpened(opened))
        { openedCorpses[openedCorpsesNext] = opened; openedCorpsesNext = (openedCorpsesNext + 1) % openedCorpses.Length; }
        long now = Environment.TickCount64;
        if (now - openedCorpsesPruned < 2000 || modernScene is null) return;
        openedCorpsesPruned = now;
        for (int i = 0; i < openedCorpses.Length; i++)
        {
            if (openedCorpses[i] == 0) continue;
            bool present = false;
            foreach (var obj in modernScene.Objects) if (obj.GameId == openedCorpses[i] && obj.ObjectType == 0x19) { present = true; break; }
            if (!present) openedCorpses[i] = 0;
        }
    }

    private async Task LocalAction(uint id, string action)
    {
        bool loot = action is "loot" or "approach" && IsCorpse(id), follow = action == "approach";
        // The verbs wait until the target request is on the wire, so a pressed verb (Follow is Move 12, which
        // follows the server's target) always reaches the server after this object was selected.
        localPending = null;
        await RequestTarget(id);
        if (loot || follow) localPending = (id, loot, follow, false, false, false, false, Environment.TickCount64);
    }

    private bool IsCorpse(uint id)
    {
        if (modernScene is null) return false;
        foreach (var obj in modernScene.Objects) if (obj.GameId == id) return obj.ObjectType == 0x19;
        return false;
    }

    /// <summary>The server's verb list for the object, once the scene's selected target is that object. The scanner's
    /// buttons are not used: right after a new selection they still carry the previous target's list (its Follow
    /// enabled), stamped with the new object's id, until the scene for the new selection is published.</summary>
    private ImmutableArray<uint> LocalVerbs(uint id) => modernScene?.Hud is { } hud &&
        (hud.HasLocalTargetSelection ? hud.LocalSelectedTargetId : hud.CurrentTargetId) == id && !hud.CurrentTargetVerbs.IsDefault
        ? hud.CurrentTargetVerbs : [];

    /// <summary>The object's verb of a kind (VerbEnum in the upper word, the server's disabled status in the lower).</summary>
    private uint? LocalVerb(uint id, uint kind)
    {
        foreach (uint verb in LocalVerbs(id)) if ((verb & 0xFFFF0000u) == kind) return verb;
        return null;
    }

    /// <summary>Each frame: press the waiting verb once it is offered enabled; stop when the loot opened, the
    /// target changed, or after a while (an approach can take a minute).</summary>
    private void UpdateLocalPending()
    {
        TrackOpenedCorpses();
        if (localPending is not { } p) return;
        long age = Environment.TickCount64 - p.Since;
        bool warping = modernScene?.Hud.Cockpit?.Warp is { IsEngaged: true };
        if (p.Loot && Inventory.LootHulkId == p.Id || age > (p.Follow ? 90000 : 20000) ||
            age > 3000 && !warping && modernScene?.Hud.CurrentTargetId is uint current && current != p.Id)
        { localPending = null; return; }
        if (warping && p.WarpSent) p.WarpSeen = true;
        if (p.Follow && !p.FollowSent)
        {
            uint? follow = LocalVerb(p.Id, FollowVerb);
            if (follow is { } enabled && (enabled & 0xFFFF) == 0) { p.FollowSent = true; _ = Scanner.ExecuteVerbSafelyAsync(p.Id, enabled); }
            // The server offers Follow only for some objects (players, some ships) and only near them: asteroids and
            // most others have none. Without an enabled Follow, warp to it once, as the warp button does with it
            // selected (the route ends at the object), once the server has it as the target.
            else if (!p.WarpSent && LocalWarpRequested is { } warp &&
                modernScene?.Hud.CurrentTargetId == p.Id && modernScene.Hud.Cockpit?.Warp is { CanActivate: true, IsEngaged: false })
            { p.WarpSent = true; p.Since = Environment.TickCount64; _ = warp(); }
            // The server did not start the warp (it says why in chat): nothing more to wait for.
            else if (p.WarpSent && !p.WarpSeen && age > 10000) { localPending = null; return; }
            // The warp brought the ship there: with no Follow to press now, the approach is done.
            else if (p.WarpSeen && !warping && !p.Loot) { localPending = null; return; }
        }
        if (p.Loot && LocalVerb(p.Id, LootVerb) is { } loot)
        {
            // The server re-enables the button when the ship comes in range: then it is pressed (once per enabling).
            if ((loot & 0xFFFF) != 0) p.LootSent = false;
            else if (!p.LootSent) { p.LootSent = true; _ = Scanner.ExecuteVerbSafelyAsync(p.Id, loot); }
        }
        if (!p.Loot && p.FollowSent) { localPending = null; return; }
        localPending = p;
    }

    private bool LocalWarpReady => modernScene?.Hud.Cockpit?.Warp is { CanActivate: true };

    /// <summary>What a waiting corpse row says: the Loot button's retail status ("Too far" and so on) or "Opening".</summary>
    private string? LocalStatus(uint id)
    {
        if (localPending is not { } waiting || waiting.Id != id) return null;
        // An approach waits on the Follow button: what it says while it cannot be pressed yet.
        if (!waiting.Loot)
            return LocalVerb(id, FollowVerb) is { } follow ? (follow & 0xFFFF) switch
            {
                0 => "Approaching",
                2 => waiting.WarpSent ? "Warping" : LocalWarpReady ? "Too far" : "Cannot warp yet",
                _ => waiting.WarpSent ? "Warping" : "Cannot follow",
            } : waiting.WarpSent ? "Warping" : LocalWarpReady || modernScene?.Hud.CurrentTargetId != id ? "Approaching" : "Cannot warp yet";
        var p = waiting;
        if (LocalVerb(id, LootVerb) is not { } loot) return p.Follow ? "Approaching" : "Opening";
        return (loot & 0xFFFF) switch
        {
            0 => "Opening",
            1 => "In a group",
            2 => p.Follow && p.FollowSent ? "Approaching" : "Too far",
            _ => "Unavailable",
        };
    }
}
