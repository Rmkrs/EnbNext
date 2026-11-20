namespace EnbNext.Net7.Protocol;

public sealed record Net7AvatarRoster(
    IReadOnlyList<Net7AvatarSlot> Slots,
    byte[] RoutingHeaderRaw,
    IReadOnlyList<Net7GalaxyRoutingRecord> RoutingRecords)
{
    public Net7AvatarSlot GetSelectableSlot(int slotIndex)
    {
        if (slotIndex is < 0 or >= Net7AvatarRosterCodec.SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slotIndex), "Character selection is the validated roster index 0..4.");

        Net7AvatarSlot slot = Slots[slotIndex];
        if (slot.IsEmpty)
            throw new InvalidOperationException($"Character slot {slotIndex} is empty.");

        return slot;
    }

    /// <summary>
    /// Retail 00769400 matches the selected slot shard without testing the
    /// galaxy-list selectable flag. The chosen Master
    /// IPv4 candidate must be nonzero. Route resolution is recomputed per call.
    /// </summary>
    public Net7GalaxyRoutingRecord ResolveUsableRouteForSlot(int slotIndex)
        => TryResolveUsableRouteForSlot(slotIndex) ?? throw new InvalidOperationException(
            $"No usable current roster route exists for character slot {slotIndex} shard {GetSelectableSlot(slotIndex).Meta.ShardId}.");

    /// <summary>00769400: null when the last matching route has Master IPv4 0 or none matches;
    /// the selection controller then shows its "galaxy ... unavailable" notice and sends nothing.</summary>
    public Net7GalaxyRoutingRecord? TryResolveUsableRouteForSlot(int slotIndex)
    {
        Net7AvatarSlot slot = GetSelectableSlot(slotIndex);
        // Exact current compatibility ordering: the retail/proxy
        // resolver overwrites its candidate for every matching route, so the
        // last matching usable entry wins. Recompute from the ordered roster on
        // every selection attempt rather than retaining a prior candidate.
        Net7GalaxyRoutingRecord? route = RoutingRecords.LastOrDefault(r =>
            r.ShardId == slot.Meta.ShardId);
        return route is null || route.MasterIpv4Raw == 0 ? null : route;
    }
}

public sealed record Net7AvatarSlot(Net7AvatarSlotMeta Meta, Net7AvatarData Avatar)
{
    public bool IsEmpty => Meta.AvatarId == 0;
}

public sealed record Net7AvatarSlotMeta(
    uint Slot,
    uint Sector,
    uint ShardId,
    uint SaveCount,
    ulong AvatarId,
    ulong AccountId,
    uint AdminLevel,
    uint GmFlag,
    uint CombatLevel,
    uint ExploreLevel,
    uint TradeLevel,
    string SectorName);

public sealed record Net7AvatarData(
    string FirstName,
    string LastName,
    uint AvatarType,
    byte UnknownU8,
    byte AvatarVersion,
    uint Race,
    uint Profession,
    uint Gender,
    uint MoodType,
    byte Personality,
    byte Nlp,
    byte BodyType,
    byte PantsType,
    byte HeadType,
    byte HairNum,
    byte EarNum,
    byte GoggleNum,
    byte BeardNum,
    byte WeaponHipNum,
    byte WeaponUniqueNum,
    byte WeaponBackNum,
    byte HeadTextureNum,
    byte TattooTextureNum,
    float[] TattooOffset,
    float[] HairColor,
    float[] BeardColor,
    float[] EyeColor,
    float[] SkinColor,
    float[] ShirtPrimaryColor,
    float[] ShirtSecondaryColor,
    float[] PantsPrimaryColor,
    float[] PantsSecondaryColor,
    uint ShirtPrimaryMetal,
    uint ShirtSecondaryMetal,
    uint PantsPrimaryMetal,
    uint PantsSecondaryMetal,
    string Skeleton,
    uint[] BodyWeight,
    uint[] HeadWeight);

public sealed record Net7GalaxyRoutingRecord(
    string GalaxyName,
    uint ShardId,
    uint MasterIpv4Raw,
    ushort RoutingPortRaw,
    uint CurrentLoad,
    uint Capacity,
    ushort EnabledSelectableGate);
