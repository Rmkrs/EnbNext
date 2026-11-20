namespace EnbNext.Client.Models;

/// <summary>
/// Client-facing view of a roster slot. These roster/avatar
/// scalars are full-width u32 wire values; keep that width here instead of
/// reintroducing the old checked Int32 presentation narrowing.
/// </summary>
public sealed record CharacterSummary(
    int Slot,
    ulong AvatarId,
    ulong AccountId,
    string FirstName,
    string LastName,
    uint Race,
    uint Profession,
    uint Gender,
    uint CombatLevel,
    uint ExploreLevel,
    uint TradeLevel,
    uint SectorId,
    string SectorName,
    uint ShardId,
    uint SaveCount,
    uint AdminLevel,
    uint GmFlag)
{
    public EnbNext.Presentation.EnbSceneAvatarSnapshot? Appearance { get; init; }
    public string GalaxyName { get; init; } = string.Empty;
    public uint GalaxyCurrentLoad { get; init; }
    public uint GalaxyCapacity { get; init; }
    public bool IsEmpty => AvatarId == 0;
    public ulong OverallLevel => (ulong)CombatLevel + ExploreLevel + TradeLevel;

    public string DisplayName => IsEmpty
        ? $"Slot {Slot}: <empty>"
        : $"Slot {Slot}: {FullName}  [{ClassName}]  CL{CombatLevel}/EL{ExploreLevel}/TL{TradeLevel}";

    public string FullName => string.IsNullOrWhiteSpace(LastName) ? FirstName : $"{FirstName} {LastName}";
    public string RaceName => IsEmpty ? string.Empty : CharacterClass.RaceName(Race);
    public string ClassName => IsEmpty ? string.Empty : CharacterClass.ClassName(Race, Profession);
}
