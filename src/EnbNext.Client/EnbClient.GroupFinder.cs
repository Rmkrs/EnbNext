using EnbNext.Presentation;

namespace EnbNext.Client;

public sealed partial class EnbClient
{
    public Task<bool> GroupFinderAsync(EnbGroupFinderRequest request, CancellationToken cancellationToken = default)
    {
        if (_net7 is not {} session || State != ClientLifecycleState.InWorld) return Task.FromResult(false);
        return session.ReadPresentationFrame((world, time) =>
        {
            if (!ReferenceEquals(session, _net7) || request.Character != session.FixedSessionId ||
                LatestPresentationSnapshot is not {} snapshot ||
                snapshot.Objects.FirstOrDefault(o => o.IsLocalShip)?.RenderIdentity != request.ShipOwner)
                return Task.FromResult(false);
            if (snapshot.IsTransitioning && request.Action != EnbGroupFinderAction.CloseList) return Task.FromResult(false);
            var social = session.Gameplay.Social;
            var group = session.PlayerSemantics.GetGroupState();
            switch (request.Action)
            {
                case EnbGroupFinderAction.Search:
                    social.SetGroupFinderWindow(true);
                    OnNet7PresentationStateChanged(time);
                    return session.RequestGroupFinderAsync(cancellationToken);
                case EnbGroupFinderAction.CloseList:
                    social.SetGroupFinderWindow(false);
                    OnNet7PresentationStateChanged(time);
                    return Task.FromResult(true);
                case EnbGroupFinderAction.Option:
                    return EnbGroupFinder.AllowsOption(group.IsInGroup, group.IsGroupLeader, request.Option)
                        ? session.SendGroupFinderOptionAsync(request.Option, request.Enabled, cancellationToken) : Task.FromResult(false);
                case EnbGroupFinderAction.Leave:
                case EnbGroupFinderAction.Disband:
                    return group.IsInGroup && group.IsGroupLeader == (request.Action == EnbGroupFinderAction.Disband)
                        ? session.LeaveGroupAsync(cancellationToken) : Task.FromResult(false);
                case EnbGroupFinderAction.Target:
                    return social.GroupFinderWindowActive && social.GroupFinderRevision == request.Revision && request.CandidateId != 0 &&
                        social.GroupFinderRows.Any(r => r.ObjectId == request.CandidateId) && world.Objects.FindFirst(request.CandidateId) is not null
                        ? RequestTargetAsync(request.CandidateId, cancellationToken) : Task.FromResult(false);
                default: return Task.FromResult(false);
            }
        });
    }
}
