using EnbNext.Presentation;

namespace EnbNext.Client;

public sealed partial class EnbClient
{
    public Task<bool> PlayerTradeAsync(EnbPlayerTradeRequest request,CancellationToken cancellationToken=default)
    {
        if(_net7 is not {} session || State!=ClientLifecycleState.InWorld)return Task.FromResult(false);
        return session.ReadPresentationFrame((world,clientTime)=>
        {
            var snapshot=LatestPresentationSnapshot;
            var trade=session.Gameplay.TradeDomain;
            if(!ReferenceEquals(session,_net7) || request.Character!=session.FixedSessionId || snapshot is not {IsTransitioning:false} ||
                snapshot.Objects.FirstOrDefault(o=>o.IsLocalShip)?.RenderIdentity!=request.ShipOwner ||
                trade.SessionRevision!=request.Revision || trade.ActivePartnerId!=request.PartnerId)return Task.FromResult(false);
            if(request.Action==EnbPlayerTradeAction.Credits)
            {
                if(request.Credits<0 || request.Credits>uint.MaxValue)return Task.FromResult(false);
                try { trade.ValidateOfferedCredits(request.Credits,session.PlayerSemantics.GetAuthoritativeMoney()); }
                catch(InvalidOperationException)
                {
                    // 005E39F0 reports type 0x11 locally; no packet is emitted.
                    session.Gameplay.AddMessage(new(0x11,"Insufficient credits."));
                    OnNet7PresentationStateChanged(clientTime);
                    return Task.FromResult(true);
                }
            }
            return request.Action switch
            {
                EnbPlayerTradeAction.Cancel=>session.CancelTradeAsync(cancellationToken),
                EnbPlayerTradeAction.Confirm=>session.ConfirmTradeAsync(cancellationToken),
                EnbPlayerTradeAction.Credits=>session.SetTradeCreditsAsync(request.Credits,cancellationToken),
                _=>Task.FromResult(false)
            };
        });
    }
}
