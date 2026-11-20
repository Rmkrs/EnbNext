using System.Buffers.Binary;
using EnbNext.Net7.Social;

namespace EnbNext.Net7;

public sealed partial class Net7Session
{
    private readonly Net7AutomaticActions automaticActions = new();
    private int automaticActionFlags;
    public void ConfigureAutomaticActions(bool invites, bool trade, bool formation, int leaderFormation = 0)
        => Volatile.Write(ref automaticActionFlags, (invites ? 1 : 0) | (trade ? 2 : 0) | (formation ? 4 : 0) |
            (Math.Clamp(leaderFormation, 0, 3) << 3));

    private volatile bool formationMembersDocked;
    /// <summary>Whether any multibox member of the local group is still docked.</summary>
    public void ConfigureFormationMembersDocked(bool docked) => formationMembersDocked = docked;
    /// <summary>A multibox member of the local group has just arrived in space.</summary>
    public void RequestFormationRefresh() { lock (_worldStateSync) automaticActions.RequestFormationRefresh(); }

    private Task ApplyAutomaticActionsAsync(CancellationToken cancellationToken)
    {
        lock (_worldStateSync)
        {
            int flags = Volatile.Read(ref automaticActionFlags);
            var group = PlayerSemantics.GetGroupState();
            var social = Gameplay.Social; var trade = Gameplay.TradeDomain;
            bool docked = PlayerSemantics.ActiveStarbaseId is not null;
            bool inSpace = !docked && !World.IsTransitioning;
            uint formation = BinaryPrimitives.ReadUInt32LittleEndian(group.Formation);
            var actions = automaticActions.Observe(social.GroupInviteRevision, social.GroupInvite is not null,
                trade.SessionRevision, trade.ActivePartnerId is not null, trade.Confirmed, trade.WaitingForConfirmation,
                group.IsInGroup, group.IsGroupLeader, group.FormationName, formation,
                (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, inSpace);
            int groupAuxState = World.LocalPlayerAuxData.Properties.FirstOrDefault(p => p.Path == "LocalPlayerAuxData.GroupInfo")?.State ?? 0;
            var departure = automaticActions.ObserveDeparture(docked, inSpace, groupAuxState, group.IsInGroup,
                group.IsGroupLeader, group.FormationName, formation, (flags & 4) != 0, flags >> 3, formationMembersDocked);
            actions.Form |= departure.Form;
            uint refresh = automaticActions.ObserveFormationRefresh(inSpace, group.IsInGroup, group.IsGroupLeader, formation, flags >> 3);
            if (!actions.Invite && !actions.Trade && !actions.Form && departure.Create == 0 && refresh == 0) return Task.CompletedTask;
            Task invite = Task.CompletedTask;
            if (actions.Invite)
            {
                invite = AcceptGroupInviteAsync(cancellationToken);
                NotifyPresentationStateChanged();
            }
            return Task.WhenAll(invite,
                actions.Trade ? ConfirmTradeAsync(cancellationToken) : Task.CompletedTask,
                actions.Form ? SendGroupCtaPanelRequestAsync(Net7GroupCtaPanelRequestType.Request7, cancellationToken) : Task.CompletedTask,
                departure.Create != 0 ? SendGroupCtaPanelRequestAsync((Net7GroupCtaPanelRequestType)departure.Create, cancellationToken) : Task.CompletedTask,
                refresh != 0 ? SendGroupCtaPanelRequestAsync((Net7GroupCtaPanelRequestType)refresh, cancellationToken) : Task.CompletedTask);
        }
    }
}
