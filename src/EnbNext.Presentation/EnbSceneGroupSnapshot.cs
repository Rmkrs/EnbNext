using System.Collections.Immutable;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Social;

namespace EnbNext.Presentation;

public sealed record EnbSceneGroupMember(int Index, string Name, uint GameId, int Position)
{ public long PresentationLifetime {get;init;} }
public sealed record EnbSceneGroupSnapshot(bool IsLeader, string FormationName, int Position,
    ImmutableArray<EnbSceneGroupMember> Members, string? Invitation, bool FormationVisualEnabled,
    ImmutableHashSet<uint> HighlightedMembers)
{
    public uint Formation { get; init; }
    public int AuxDataState { get; init; }
    public ulong AuxRevision { get; init; }
    public Net7GroupFinderSettings FinderSettings { get; init; } = new(false, false, false, false, false);
    public long FinderRevision { get; init; }
    public ImmutableArray<Net7GroupFinderCandidate> FinderRows { get; init; } = [];
    public Guid AttentionStream {get;init;}
    public ImmutableArray<Net7GroupAttentionEvent> AttentionEvents {get;init;}=[];
    public Guid SymbolStream { get; init; }
    public ImmutableArray<Net7SocialState.GroupSymbol> Symbols { get; init; } = [];
    public long InvitationRevision { get; init; }
    public bool IsInGroup => Members.Any(m => m.Name.Length != 0);
    internal static EnbSceneGroupSnapshot From(Net7GroupStateView group, Net7SocialState? social)
        => new(group.IsGroupLeader, group.FormationName, group.Position,
            group.Members.Select((m,i)=>new EnbSceneGroupMember(i,m.Name,m.GameId,m.Position)).ToImmutableArray(),
            social?.GroupInvite?.InviteText, social?.GroupFormationVisualEnabled ?? false,
            social?.HighlightedGroupAvatarIds.ToImmutableHashSet() ?? ImmutableHashSet<uint>.Empty)
        {AttentionStream=social?.GroupAttentionStream??Guid.Empty,AttentionEvents=social?.GroupAttentionEvents.ToImmutableArray()??[],SymbolStream=social?.StreamId ?? Guid.Empty, Symbols=social?.GroupSymbols.ToImmutableArray() ?? [], InvitationRevision=social?.GroupInviteRevision ?? 0, Formation=System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(group.Formation),
            FinderRevision=social?.GroupFinderRevision ?? 0, FinderRows=social?.GroupFinderRows.ToImmutableArray() ?? [],
            FinderSettings=new(group.LookingForGroup,group.AllowGroupInvite,group.ForceAutoSplit,group.RestrictedLootingRights,group.AutoReleaseLootingRestrictions)};
}

public enum EnbGroupAction { Accept, Reject, Leave, TargetMember, Assist, AskMember, AskGroup, Kick, Pipe, Slot, Block, Form, LeaveFormation, BreakFormation, Symbol, ToggleFormation }
public sealed record EnbGroupRequest(uint Character, ulong ShipOwner, EnbGroupAction Action,
    int MemberIndex = -1, uint MemberId = 0, string? MemberName = null, string? Invitation = null,
    string? SymbolAsset = null, bool ContextSymbol = false, long InvitationRevision = 0);
