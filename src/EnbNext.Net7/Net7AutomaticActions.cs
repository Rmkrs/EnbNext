namespace EnbNext.Net7;

// Event observations belong to one session. No timer, retry, or optimistic state.
internal sealed class Net7AutomaticActions
{
    private long invitation;
    private ulong tradeRevision;
    private bool confirmed;
    private string formationName = "";
    private bool pendingFormation;

    public (bool Invite, bool Trade, bool Form) Observe(long inviteRevision, bool hasInvite,
        ulong revision, bool hasPartner, bool peerConfirmed, bool waiting,
        bool inGroup, bool leader, string name, uint formation,
        bool acceptInvites, bool acceptTrade, bool formUp, bool inSpace = true)
    {
        bool invite = hasInvite && inviteRevision != invitation && acceptInvites;
        invitation = inviteRevision;
        bool trade = hasPartner && peerConfirmed && (revision != tradeRevision || !confirmed) && !waiting && acceptTrade;
        tradeRevision = revision; confirmed = hasPartner && peerConfirmed;
        if (!inGroup || leader) { formationName = ""; pendingFormation = false; }
        else
        {
            if (name != formationName) pendingFormation = name.Length != 0 && formUp;
            formationName = name;
        }
        // A formation announced while docked stays pending until the ship is in space.
        bool form = pendingFormation && inSpace && inGroup && !leader && name.Length != 0 && formation == 0 && formUp;
        if (form || !formUp || formation != 0) pendingFormation = false;
        return (invite, trade, form);
    }

    private bool departed;

    /// <summary>Multibox launch. After leaving a station, once the first materialized
    /// GroupInfo is seen in space, a formed-out leader creates the configured formation
    /// (CTA 6 Pipe, 4 Slot, 5 Block) and a member with Auto form up joins the existing one.
    /// The server only offers a formation to members in space (retail shows the same), so the
    /// leader waits while any of its multibox group members is still docked.</summary>
    public (bool Form, uint Create) ObserveDeparture(bool docked, bool inSpace, int groupAuxState,
        bool inGroup, bool leader, string name, uint formation, bool formUp, int leaderFormation,
        bool membersDocked = false)
    {
        if (docked) { departed = true; return (false, 0); }
        if (!departed || !inSpace || groupAuxState == 0) return (false, 0);
        if (leader && inGroup && formation == 0 && leaderFormation != 0 && membersDocked) return (false, 0);
        departed = false;
        if (!inGroup || formation != 0) return (false, 0);
        if (leader)
            return (false, leaderFormation switch { 1 => 6u, 2 => 4u, 3 => 5u, _ => 0u });
        return (formUp && name.Length != 0, 0);
    }

    private bool refreshRequested;
    private uint refreshType;

    /// <summary>Request that the leader re-offers its formation (a member arrived in space).</summary>
    public void RequestFormationRefresh() => refreshRequested = true;

    /// <summary>A member that undocks into an existing formation is never offered it by the
    /// server. The leader re-offers it the way a player does by hand: break the formation
    /// (CTA 9), then, once GroupInfo shows it gone, create the same type again (4/5/6).</summary>
    public uint ObserveFormationRefresh(bool inSpace, bool inGroup, bool leader, uint formation, int leaderFormation)
    {
        if (!inGroup || !leader || leaderFormation == 0) { refreshRequested = false; refreshType = 0; return 0; }
        if (!inSpace) return 0;
        if (refreshType != 0)
        {
            if (formation != 0) return 0;
            uint create = refreshType; refreshType = 0;
            return create;
        }
        if (!refreshRequested) return 0;
        refreshRequested = false;
        if (formation is not (4 or 5 or 6)) return 0;
        refreshType = formation;
        return 9;
    }
}
