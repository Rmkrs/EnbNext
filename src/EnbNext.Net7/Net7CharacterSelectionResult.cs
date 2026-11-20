using EnbNext.Net7.Protocol;

namespace EnbNext.Net7;

/// <summary>Terminal state: selected roster slot plus validated session identity.</summary>
public sealed record Net7CharacterSelectionResult(
    int SlotIndex,
    Net7AvatarSlot Character,
    Net7GalaxyRoutingRecord RoutingRecord,
    uint FixedSessionId,
    ushort ResponseOpcode,
    Net7SectorJoinClientData SectorJoinData);
