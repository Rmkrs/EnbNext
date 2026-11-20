using System.Collections.Immutable;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Social;

namespace EnbNext.Presentation;

public sealed record EnbSceneGuildSnapshot(Net7GuildPermissions Permissions,
    ImmutableArray<Net7GuildRankName> Ranks, ImmutableArray<Net7GuildRosterEntry> Members,
    ImmutableArray<string> FirstLines, ImmutableArray<string> SecondLines, Net7GuildPrompt? Prompt)
{
    public ImmutableArray<string> DisplayLines { get; init; } = [];
    public long DisplayRevision { get; init; }
    public static EnbSceneGuildSnapshot From(Net7GameplayState state) => new(
        state.GuildPermissions ?? new(0, uint.MaxValue, uint.MaxValue, uint.MaxValue),
        state.GuildRankNames.ToImmutableArray(), state.Social.GuildRosterEntries.ToImmutableArray(),
        state.Social.GuildSecondaryFirstLines.ToImmutableArray(), state.Social.GuildSecondarySecondLines.ToImmutableArray(), state.GuildPrompt)
        { DisplayLines = state.Social.GuildSecondaryListLines.ToImmutableArray(), DisplayRevision = state.Social.GuildDisplayRevision };

    public bool Allows(EnbGuildAction action, int localRank, string? member)
    {
        if (action is EnbGuildAction.Open or EnbGuildAction.Close or EnbGuildAction.CloseDisplay or EnbGuildAction.Accept or EnbGuildAction.Reject) return true;
        if (localRank == -1) return action == EnbGuildAction.Create;
        var selected = Members.FirstOrDefault(m => m.Identity == member);
        int rank = selected is null ? -1 : unchecked((int)Net7SocialGuildPolicy.ResolveRankNumber(Ranks, selected.RankName));
        var c = Net7SocialGuildPolicy.EvaluateControls(Permissions, rank, localRank);
        return action switch
        {
            EnbGuildAction.Stats => c.Stats, EnbGuildAction.Contributions => c.Contributions && selected is not null,
            EnbGuildAction.Leave => c.Leave, EnbGuildAction.Recruit => c.Recruit,
            EnbGuildAction.Remove => c.CanRemoveSelected, EnbGuildAction.Promote => c.CanPromoteSelected,
            EnbGuildAction.Demote => c.CanDemoteSelected, EnbGuildAction.Tag => c.Tag && selected is not null,
            EnbGuildAction.Message => c.Message, EnbGuildAction.Disband => c.Disband,
            EnbGuildAction.Public => c.MakePublic, EnbGuildAction.Private => c.MakePrivate,
            _ => false
        };
    }
}

public enum EnbGuildAction { Open, Close, CloseDisplay, Recruit, Remove, Promote, Demote, Disband, Tag, Message, Public, Private, Leave, Contributions, Stats, Create, Accept, Reject }
public sealed record EnbGuildRequest(uint Character, ulong ShipOwner, EnbGuildAction Action,
    string? Member = null, string? Text = null, Net7GuildPrompt? Prompt = null);
