using EnbNext.Presentation;

namespace EnbNext.Client;

public sealed partial class EnbClient
{
    public Task<bool> StationPlayerAsync(EnbStationPlayerInteraction request,CancellationToken cancellationToken=default)
    {
        if(_net7 is not {} session || State!=ClientLifecycleState.InWorld)return Task.FromResult(false);
        return session.ReadPresentationFrame((world,_)=>
        {
            if(!ReferenceEquals(session,_net7) || session.FixedSessionId!=request.Character || world.IsTransitioning ||
                world.PresentationHandoffVersion!=request.HandoffVersion || session.PlayerSemantics.ActiveStarbaseId!=request.StarbaseId ||
                !world.StationAvatars.TryGetValue(request.AvatarId,out var peer) || peer.RoomType!=request.Room || (peer.ActionFlag&0x3e)!=0 ||
                request.AvatarId==LatestPresentationSnapshot?.LocalAvatarObjectId)return Task.FromResult(false);
            //63DFE0 uses the selected room player, independently of1130's space target.
            //Leadership enabled the row when645510 built it; the result consumer does not recheck it.
            return request.Action switch
            {
                EnbStationPlayerAction.GroupInvite=>session.InviteStationPlayerToGroupAsync(request.AvatarId,cancellationToken),
                EnbStationPlayerAction.Trade=>session.RequestTradeAsync(request.AvatarId,cancellationToken),
                _=>Task.FromResult(false)
            };
        });
    }
}
