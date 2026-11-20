using EnbNext.Client;
using EnbNext.Presentation;

namespace EnbNext.App;

/// <summary>
/// Next multibox workaround for a Net-7 server omission: after a sector change the server can leave out the formation
/// position message (0x0041) for a follower to the leader, so the leader keeps that follower where it arrived while
/// it really flies in formation (no other movement is sent for a formation member). When one of the player's own
/// clients says it is in the formation but the leader's client has it unlinked for a few seconds, the leader switches
/// the formation to another type and back, as a player would by hand; the server then sends every member's position.
/// A formation is only changed outside warp (not charging or warping, in space, not loading): a mismatch found during
/// a warp waits for the warp to end, and the leader's auto pilot waits for the repair before its next step.
/// </summary>
internal sealed partial class SdlApplication
{
    private long _formationUnlinkedSince, _formationRepairAt;
    private int _formationRepairStep;
    private uint _formationRepairType;
    private const long FormationUnlinkedGrace = 6000, FormationRepairInterval = 20000, FormationRepairStepTimeout = 5000;

    /// <summary>Formation changes are possible: in space, not loading, not charging or in warp.</summary>
    private static bool FormationChangeable(IEnbSceneFrame scene)
        => !scene.IsTransitioning && scene.ActiveStarbaseId is null &&
           scene.Hud.Cockpit?.Warp is { IsEngaged: false, Incapacitated: false, PresentationMode: 0 };

    /// <summary>The leader's auto pilot waits while a repair is due or under way.</summary>
    private bool FormationRepairHolds()
    {
        long now = Environment.TickCount64;
        return _formationRepairStep != 0 || _formationUnlinkedSince != 0 && now - _formationUnlinkedSince >= FormationUnlinkedGrace &&
            now - _formationRepairAt >= FormationRepairInterval;
    }

    private void RepairFormationLinks(EnbClient client)
    {
        var scene = client.LatestPresentationSnapshot;
        var context = client.MultiboxContext;
        if (!multiboxOptions.Enabled || multibox is not { } bus || scene is null ||
            scene.Hud.Group is not { IsLeader: true, IsInGroup: true } group)
        { _formationUnlinkedSince = 0; _formationRepairStep = 0; return; }
        long now = Environment.TickCount64;
        bool changeable = context is not null && FormationChangeable(scene);
        if (_formationRepairStep == 1)
        {
            // The other type is in place: back to the formation the player chose, as soon as that is possible.
            if (group.Formation is 4 or 5 or 6 && group.Formation != _formationRepairType)
            {
                if (changeable) { dispatch.Observe(client.RequestFormationAsync(_formationRepairType)); _formationRepairStep = 2; _formationRepairAt = now; }
            }
            else if (now - _formationRepairAt > FormationRepairStepTimeout) { _formationRepairStep = 0; _formationRepairAt = now; } // Refused: try again later.
            return;
        }
        if (_formationRepairStep == 2)
        {
            if (group.Formation == _formationRepairType || group.Formation is not (4 or 5 or 6)) { _formationRepairStep = 0; _formationUnlinkedSince = 0; return; }
            if (now - _formationRepairAt > FormationRepairStepTimeout && changeable)
            { dispatch.Observe(client.RequestFormationAsync(_formationRepairType)); _formationRepairAt = now; } // Asked again until it is back.
            return;
        }
        if (context is null || group.Formation is not (4 or 5 or 6) || scene.IsTransitioning) { _formationUnlinkedSince = 0; return; }
        string? unlinked = null;
        foreach (var peer in bus.Peers)
        {
            if (peer.Server != _savedAccountServer || !peer.InFormation || peer.Context is not { } member || member.Sector != context.Sector ||
                !group.Members.Any(m => m.GameId == peer.Avatar)) continue;
            if (scene.Objects.FirstOrDefault(o => o.GameId == member.Ship) is { } ship && ship.Spatial?.FormationLeaderId != context.Ship)
            { unlinked = peer.Name; break; }
        }
        if (unlinked is null) { _formationUnlinkedSince = 0; return; }
        if (_formationUnlinkedSince == 0) { _formationUnlinkedSince = now; return; }
        if (now - _formationUnlinkedSince < FormationUnlinkedGrace || now - _formationRepairAt < FormationRepairInterval || !changeable) return;
        _formationRepairType = group.Formation;
        AppendDiagnostic("Formation repair: " + unlinked + " is in the formation but not placed in it here; switching the formation and back");
        dispatch.Observe(client.RequestFormationAsync(group.Formation == 6 ? 4u : 6u));
        _formationRepairStep = 1; _formationRepairAt = now;
    }
}
