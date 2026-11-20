using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Social;

/// <summary>
/// Current-retail group producer policy. Packet codecs remain generic;
/// this layer bounds the combinations the current client can actually produce.
/// </summary>
public static class Net7SocialGroupPolicy
{
    public static Net7GroupRequestPlan PlanTargetAction(
        uint localPlayerId,
        uint? selectedTargetId,
        Net7PlayerAction action)
    {
        if (action is not Net7PlayerAction.GroupInvite and not Net7PlayerAction.GroupKick)
            throw new ArgumentOutOfRangeException(nameof(action), "Target-backed SOCIAL-05 actions are invite or kick.");

        if (!selectedTargetId.HasValue || selectedTargetId.Value == 0)
        {
            string localError = action == Net7PlayerAction.GroupInvite
                ? "Cannot invite - no target."
                : "Target required.";
            return Net7GroupRequestPlan.NoSend(localError);
        }

        return new Net7GroupRequestPlan(
            true,
            0x002C,
            Net7GameplayWire.BuildAction(localPlayerId, action, selectedTargetId.Value, 0),
            null);
    }

    /// <summary>005949AA..00594A72: a member whose object is present is kicked by ID (002C 0F); one not in view by name
    /// (002D 0F) (GROUP-D03).</summary>
    public static Net7GroupRequestPlan PlanMemberRowKick(uint localPlayerId, Net7GroupMemberView member, bool present = true)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (member.GameId != 0 && (present || string.IsNullOrEmpty(member.Name)))
        {
            return new Net7GroupRequestPlan(
                true,
                0x002C,
                Net7GameplayWire.BuildAction(localPlayerId, Net7PlayerAction.GroupKick, member.GameId, 0),
                null);
        }

        if (string.IsNullOrEmpty(member.Name))
            return Net7GroupRequestPlan.NoSend();

        return new Net7GroupRequestPlan(
            true,
            0x002D,
            Net7GameplayWire.BuildAction2(localPlayerId, 0x0F, member.Name, 0),
            null);
    }

    public static Net7GroupRequestPlan PlanPanelCta(
        uint localPlayerId,
        bool isGroupLeader,
        Net7GroupCtaPanelRequestType requestType)
    {
        bool reachable = requestType switch
        {
            Net7GroupCtaPanelRequestType.Request4 or
            Net7GroupCtaPanelRequestType.Request5 or
            Net7GroupCtaPanelRequestType.Request6 or
            Net7GroupCtaPanelRequestType.Request9 => isGroupLeader,
            Net7GroupCtaPanelRequestType.Request7 or
            Net7GroupCtaPanelRequestType.Request8 => !isGroupLeader,
            _ => false,
        };
        if (!reachable)
            return Net7GroupRequestPlan.NoSend();

        return new Net7GroupRequestPlan(
            true,
            0x00BC,
            Net7GameplayWire.BuildCtaRequest(localPlayerId, 0, (uint)requestType),
            null);
    }

    public static Net7GroupRequestPlan PlanFormationToggle(uint localPlayerId,
        bool inGroup, bool leader, int auxState, uint formation, string formationName)
    {
        // 005946D0 command 47: retained GroupInfo +560 wins over +4D8 name.
        if (!inGroup || leader || auxState == 0) return Net7GroupRequestPlan.NoSend();
        if (formation != 0) return PlanPanelCta(localPlayerId, false, Net7GroupCtaPanelRequestType.Request8);
        return formationName.Length != 0
            ? PlanPanelCta(localPlayerId, false, Net7GroupCtaPanelRequestType.Request7)
            : Net7GroupRequestPlan.NoSend();
    }

    public static Net7GroupRequestPlan PlanTopLevelCta12(uint localPlayerId)
        => new(
            true,
            0x00BC,
            Net7GameplayWire.BuildCtaRequest(localPlayerId, uint.MaxValue, 12),
            null);

    public static Net7GroupRequestPlan PlanMemberCta12(uint localPlayerId, Net7GroupMemberView member)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (member.GameId == 0)
            return Net7GroupRequestPlan.NoSend();
        return new Net7GroupRequestPlan(
            true,
            0x00BC,
            Net7GameplayWire.BuildCtaRequest(localPlayerId, member.GameId, 12),
            null);
    }
}
