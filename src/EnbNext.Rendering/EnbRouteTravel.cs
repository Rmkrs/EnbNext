using System.Collections.Immutable;

namespace EnbNext.Rendering;

/// <summary>
/// What the pilot can travel with beyond ordinary gates: Smuggler's Run (The Red Dragon gates, standing 2000 or more
/// with The Red Dragon) and the wormholes that may be used (Next route option). Multiboxing, Smuggler's Run needs
/// every one of the player's own clients in the group, and a wormhole one of them who can open it.
/// </summary>
public sealed record EnbRouteTravel(bool SmugglersRun, ImmutableArray<EnbRouteWormhole> Wormholes)
{
    public static EnbRouteTravel None { get; } = new(false, []);
    public bool Equals(EnbRouteTravel? other) => other is not null && SmugglersRun == other.SmugglersRun && Wormholes.SequenceEqual(other.Wormholes);
    public override int GetHashCode() => HashCode.Combine(SmugglersRun, Wormholes.Length);
}

/// <summary>
/// A wormhole that can be opened: its destination sector name (the ability "X Gate" opens X), the ability that opens
/// it, and who opens it (the character's name; empty for this pilot).
/// </summary>
public sealed record EnbRouteWormhole(string Destination, int AbilityId, string Caster);
