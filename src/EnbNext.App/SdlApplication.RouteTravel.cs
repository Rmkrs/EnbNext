using System.Collections.Immutable;
using EnbNext.Client;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.App;

/// <summary>
/// Next route planning beyond gates: Smuggler's Run (gates of The Red Dragon, standing 2000 or more) and wormholes
/// (Navigation option "Use wormholes in routes"). Solo, the pilot's own standing and skills decide. Multiboxing, only
/// the player's own clients in the group count (other players are left out): Smuggler's Run needs every one of them,
/// and a wormhole one of them who can open it, while every one of them follows the leader's wormholes and this
/// client leads the group. Auto pilot then asks that client to open it and every client accepts it.
/// </summary>
internal sealed partial class SdlApplication
{
    private static readonly string[] WormholeFamilies = ["Create Wormhole", "Extended Wormhole"];

    /// <summary>The pilot's standing with The Red Dragon, if the character data has it.</summary>
    private static float? RedDragonStanding(EnbClient client, IEnbSceneFrame scene)
    {
        if (scene.Hud.Inventory is not { } data || client.RetailAssets?.Factions is not { } factions) return null;
        foreach (var faction in data.Factions)
            if (factions.TryResolve(faction.Name, out var definition) && definition.Name.Equals("The Red Dragon", StringComparison.OrdinalIgnoreCase))
                return faction.Reaction;
        return null;
    }

    /// <summary>The pilot's ranks of Create Wormhole and Extended Wormhole.</summary>
    private static (int Create, int Extended) WormholeRanks(IEnbSceneFrame scene)
    {
        int Rank(string family) => scene.Hud.Inventory?.Skills.FirstOrDefault(s => s.Name.Equals(family, StringComparison.OrdinalIgnoreCase))?.Level ?? 0;
        return (Rank(WormholeFamilies[0]), Rank(WormholeFamilies[1]));
    }

    /// <summary>The wormholes ranks open: each ability "X Gate" of the families at or below the rank opens X.</summary>
    private static IEnumerable<EnbRouteWormhole> Wormholes(EnbClient client, (int Create, int Extended) ranks, string caster)
    {
        if (client.RetailAssets?.ResolveShortcutSkills() is not { } catalog) yield break;
        foreach (var ability in catalog.Abilities)
        {
            int rank = ability.FamilyName.Equals(WormholeFamilies[0], StringComparison.OrdinalIgnoreCase) ? ranks.Create
                : ability.FamilyName.Equals(WormholeFamilies[1], StringComparison.OrdinalIgnoreCase) ? ranks.Extended : -1;
            if (rank <= 0 || !ability.Activated || ability.MinimumLevel > rank || !ability.Name.EndsWith(" Gate", StringComparison.Ordinal)) continue;
            yield return new(ability.Name[..^5], ability.Id, caster);
        }
    }

    /// <summary>The player's own clients in this client's group (multiboxing), on this server.</summary>
    private MultiboxState[] OwnGroupClients(MultiboxState self) => multibox?.Peers.Where(p => p.Instance != self.Instance && p.Server == self.Server &&
        self.GroupMembers.Contains(p.Avatar) && p.Avatar != self.Avatar).ToArray() ?? [];

    private sealed class RouteRegroup
    {
        public Guid[] Members = [];
        public uint Sector, Formation;
        public long Arrived, Asked;
        public bool Created;
    }
    private RouteRegroup? _regroup;
    private const long RegroupTimeout = 120000;

    /// <summary>Auto pilot waits on the wormhole's far side while the group regroups.</summary>
    private bool RegroupHolds() => _regroup is { } r && _client?.MultiboxContext?.Sector is uint sector && sector != r.Sector;

    /// <summary>
    /// After an auto pilot wormhole (multiboxing): once every follower has arrived in the new sector, the leader creates
    /// its formation again (when the wormhole ended it) and asks the followers that are not in it to join, until all are.
    /// </summary>
    private void AdvanceRegroup(EnbClient client)
    {
        if (_regroup is not { } r) return;
        if (!client.Autopilot.Active || multibox is not { } bus) { _regroup = null; return; }
        if (client.MultiboxContext is not { } context || context.Sector == r.Sector) return;
        long now = Environment.TickCount64;
        if (r.Arrived == 0) r.Arrived = now;
        if (now - r.Arrived > RegroupTimeout) { _regroup = null; client.Autopilot.Cancel("The group did not form up after the wormhole"); return; }
        var members = bus.Peers.Where(p => r.Members.Contains(p.Instance)).ToArray();
        if (members.Any(p => p.Context?.Sector != context.Sector)) return; // A follower is still on its way.
        var group = client.LatestPresentationSnapshot?.Hud.Group;
        if (group is not { IsLeader: true, IsInGroup: true }) { _regroup = null; return; }
        if (group.Formation == 0)
        {
            if (!r.Created || now - r.Asked > 10000) { r.Created = true; r.Asked = now; dispatch.Observe(client.RequestFormationAsync(r.Formation)); }
            return;
        }
        var waiting = members.Where(p => !p.InFormation).ToArray();
        if (waiting.Length == 0) { _regroup = null; return; }
        if (now - r.Asked < 5000) return;
        r.Asked = now;
        foreach (var peer in waiting)
        {
            var input = new EnbMultiboxInput(Guid.NewGuid(), "form", 0, context);
            dispatch.Observe(Ask(peer, input));
        }
        async Task Ask(MultiboxState peer, EnbMultiboxInput input)
        {
            try { await bus.SendAsync(peer.Instance, "command", input.Id, input.Action, command: input, recipient: peer.Context); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private EnbRouteTravel _routeTravel = EnbRouteTravel.None;
    private void UpdateRouteTravel(EnbClient client, MultiboxState? self)
    {
        var scene = client.LatestPresentationSnapshot;
        if (scene is null) { _routeTravel = EnbRouteTravel.None; return; }
        float? standing = RedDragonStanding(client, scene);
        bool wormholes = EffectiveQualityOfLife.RouteWormholes;
        var peers = self is null ? [] : OwnGroupClients(self);
        bool run = standing >= EnbSectorRouteGuidance.SmugglersRunStanding && peers.All(p => p.RedDragon >= EnbSectorRouteGuidance.SmugglersRunStanding);
        var holes = ImmutableArray.CreateBuilder<EnbRouteWormhole>();
        if (wormholes && (peers.Length == 0 || self is { Leader: true } && peers.All(p => p.FollowWormhole)))
        {
            holes.AddRange(Wormholes(client, WormholeRanks(scene), ""));
            foreach (var peer in peers.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                holes.AddRange(Wormholes(client, (peer.CreateWormhole, peer.ExtendedWormhole), peer.Name));
        }
        _routeTravel = new(run, holes.ToImmutable());
    }

    /// <summary>Auto pilot's wormhole step: this pilot opens it, or asks the client of the character who can.
    /// Every other own client in the group that follows the leader's wormholes is told to accept it.</summary>
    private async Task<bool> OpenRouteWormholeAsync(int ability, string caster, CancellationToken token)
    {
        if (_client is not { } client) return false;
        var bus = multibox;
        var context = client.MultiboxContext;
        var followers = bus is not null && context is not null && multiboxOptions.Enabled
            ? bus.Peers.Where(p => p.Server == _savedAccountServer && p.Context is { } c && c.Sector == context.Sector &&
                p.GroupMembers.Contains(context.Avatar) && p.FollowWormhole && client.CanLeadMultibox(context, p.Avatar)).ToArray() : [];
        if (caster.Length > 0 && !followers.Any(p => SavedAccountStore.Same(p.Name, caster))) return false;
        if (followers.Length > 0)
        {
            // On the other side the leader waits for every follower, forms up again and waits for them to join.
            var group = client.LatestPresentationSnapshot?.Hud.Group;
            uint formation = group?.Formation is 4 or 5 or 6 ? group.Formation : multiboxOptions.AutoFormation switch { 2 => 4u, 3 => 5u, _ => 6u };
            _regroup = new() { Members = [.. followers.Select(p => p.Instance)], Sector = context!.Sector, Formation = formation };
        }
        foreach (var peer in followers)
        {
            bool opens = SavedAccountStore.Same(peer.Name, caster);
            var input = new EnbMultiboxInput(Guid.NewGuid(), "wormhole", opens ? (uint)ability : uint.MaxValue, context!);
            try { await bus!.SendAsync(peer.Instance, "command", input.Id, input.Action, token, command: input, recipient: peer.Context); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { if (opens) return false; }
        }
        if (caster.Length > 0) { client.ExpectWormhole(); return true; }
        return await client.OpenWormholeAsync(ability, token);
    }
}
