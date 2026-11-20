using EnbNext.Engine;

namespace EnbNext.App;

/// <summary>
/// Next multibox "Reactor in group frame" (opt-in, Multibox menu): each client with it on publishes its reactor in whole
/// percent while in space, and the Modern UI group frame of a client with it on shows a ring gauge on the rows of its
/// own characters that share theirs. Rows of other players stay as they are.
/// </summary>
internal sealed partial class SdlApplication
{
    private int? publishedReactor;
    private long reactorNext,reactorAnnounced;
    private bool reactorPending;
    private EnbWorldUi? reactorUi;

    private void TickSharedReactor()
    {
        long now=Environment.TickCount64;if(now<reactorNext)return;reactorNext=now+250;
        var ui=worldScreen?.Owner.Ui;
        if(!ReferenceEquals(ui,reactorUi)){reactorUi=ui;if(ui is not null)ui.Inventory.MemberReactor=MemberReactor;}
        if(multibox is not {} bus)return;
        int? reactor=multiboxOptions.SharedReactor&&_client?.LatestPresentationSnapshot is {} scene&&scene.Objects.Any(o=>o.IsLocalShip)&&
            scene.Hud.Cockpit?.EnergyPercent is float energy&&float.IsFinite(energy)?(int)MathF.Round(Math.Clamp(energy,0,1)*100):null;
        if(reactor!=publishedReactor){publishedReactor=reactor;bus.Publish(bus.Current with {Reactor=reactor});reactorPending=true;}
        // Peers also poll every second; a change is announced at most twice a second so a draining reactor shows promptly.
        if(reactorPending&&now-reactorAnnounced>=500){reactorPending=false;reactorAnnounced=now;dispatch.Observe(bus.AnnounceAsync());}
    }

    private int? MemberReactor(uint gameId)
    {
        if(!multiboxOptions.SharedReactor||multibox is not {} bus)return null;
        var self=bus.Current;
        foreach(var peer in bus.Peers)
            if(peer.Avatar==gameId&&peer.Reactor is int reactor&&peer.Server==self.Server)return reactor;
        return null;
    }
}
