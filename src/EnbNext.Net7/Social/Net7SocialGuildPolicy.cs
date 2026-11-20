using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Social;

/// <summary>Direct-authority guild prompt, permission and refresh policy.</summary>
public static class Net7SocialGuildPolicy
{
    public static Net7GuildReplyPlan PlanPromptReply(uint localPlayerId, Net7GuildPrompt? prompt, bool accept)
    {
        if (prompt is null)
            return Net7GuildReplyPlan.NoSend();

        if (prompt.Opcode == 0x00C8)
            return new Net7GuildReplyPlan(true, 0x00C9, Net7GameplayWire.BuildGuildRecruitAccept(localPlayerId, accept));

        if (prompt.Opcode == 0x00D8)
        {
            if (!accept)
                return Net7GuildReplyPlan.NoSend();
            return new Net7GuildReplyPlan(true, 0x00CD, Net7GameplayWire.BuildGuildSimpleReply(15, localPlayerId, prompt.Second));
        }

        if (prompt.Opcode != 0x00CC)
            return Net7GuildReplyPlan.NoSend();

        if (prompt.Type == 0)
            return new Net7GuildReplyPlan(true, 0x00C5, Net7GameplayWire.BuildGuildLeaderAccept(localPlayerId, prompt.First, accept));

        if (!accept)
            return Net7GuildReplyPlan.NoSend();

        uint replyType = prompt.Type switch
        {
            1 => 2,
            3 => 4,
            5 => 6,
            7 => 8,
            9 => 10,
            11 => 12,
            13 => 14,
            16 => 17,
            18 => 19,
            20 => 21,
            _ => 0,
        };
        return replyType == 0
            ? Net7GuildReplyPlan.NoSend()
            : new Net7GuildReplyPlan(true, 0x00CD, Net7GameplayWire.BuildGuildSimpleReply(replyType, localPlayerId, prompt.First));
    }

    public static bool ShouldAutoRefreshRoster(uint messageType, bool guildWindowActive, bool isInGuild)
        // Current retail client fixed refresh table at 0x00B03208.
        => guildWindowActive && isInGuild && messageType is 69 or 72 or 76 or 97 or 119 or 120;

    public static uint ResolveRankNumber(IReadOnlyList<Net7GuildRankName> rankNames, string rankName)
    {
        ArgumentNullException.ThrowIfNull(rankNames);
        ArgumentNullException.ThrowIfNull(rankName);
        foreach (Net7GuildRankName entry in rankNames)
        {
            if (string.Equals(entry.Name, rankName, StringComparison.Ordinal))
                return entry.Rank;
        }
        return uint.MaxValue;
    }

    public static Net7GuildControlAvailability EvaluateControls(Net7GuildPermissions permissions, int selectedRank, int localRank)
    {
        uint mask = permissions.Mask;
        bool hasSelection = selectedRank >= 0 && localRank >= 0;
        int maxPromote = unchecked((int)permissions.MaxPromoteRank);
        int maxRemove = unchecked((int)permissions.MaxRemoveRank);
        int minDemote = unchecked((int)permissions.MinDemoteRank);

        return new Net7GuildControlAvailability(
            Stats: HasBit(mask, 0),
            Contributions: HasBit(mask, 1),
            Leave: HasBit(mask, 2),
            Recruit: HasBit(mask, 3),
            RemoveBasePermission: HasBit(mask, 4),
            Message: HasBit(mask, 5),
            Disband: HasBit(mask, 8),
            Tag: HasBit(mask, 9),
            MakePublic: HasBit(mask, 10),
            MakePrivate: HasBit(mask, 11),
            CanPromoteSelected: hasSelection && selectedRank < maxPromote && selectedRank < localRank,
            CanRemoveSelected: hasSelection && selectedRank <= maxRemove && selectedRank <= localRank,
            CanDemoteSelected: hasSelection && selectedRank > minDemote && selectedRank < localRank);
    }

    private static bool HasBit(uint mask, int bit) => (mask & (1u << bit)) != 0;
}
