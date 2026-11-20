using EnbNext.Presentation;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Client;

/// <summary>Explicit, session-local orchestration of ordinary player commands.</summary>
public sealed class EnbSectorAutopilot(
    Func<uint,CancellationToken,Task<bool>> target,
    Func<uint,ulong,IReadOnlyList<uint>,CancellationToken,Task<bool>> warp,
    Func<uint,uint,CancellationToken,Task<bool>> verb, Action<string>? diagnostic = null)
{
    private enum Phase { Idle, Select, Selected, Warp, Gate, Wormhole }
    /// <summary>Next: opens a route wormhole (its ability and the character who opens it; empty for this pilot).</summary>
    public Func<int,string,CancellationToken,Task<bool>>? Wormhole { get; set; }
    private long wormholeDeadline;
    private readonly object sync=new();
    private Phase phase;
    private CancellationTokenSource? stop;
    private bool sending;
    private Action? dispatch;
    private uint destination, sector, gate;
    private Guid session;
    public bool Active { get { lock(sync)return phase!=Phase.Idle; } }
    public string? StopReason {get;private set;}

    public void Cancel(string reason="Player action")
    {
        lock(sync)
        {
            if(phase==Phase.Idle)return;
            phase=Phase.Idle;StopReason=reason;diagnostic?.Invoke("Autopilot stopped: "+reason);
            stop?.Cancel();stop?.Dispose();stop=null;
        }
    }
    public void ObserveMessage(byte kind,string text)
    { lock(sync)if(phase is Phase.Select or Phase.Selected or Phase.Gate or Phase.Wormhole && kind is 17 or 18 or 19 or 21)Cancel(text); }

    public static bool CanStart(IEnbSceneFrame scene,bool enabled,uint? destination,uint? gate,bool restricted,bool wormhole=false)
        => enabled && destination is not null && (gate is not null || wormhole) && !restricted &&
           !scene.IsTransitioning && scene.ActiveStarbaseId is null &&
           scene.Environment?.SectorNum is uint sector && sector!=destination &&
           scene.Objects.Any(o=>o.IsLocalShip) &&
           scene.Hud.Cockpit?.Warp is {IsEngaged:false,Incapacitated:false,PresentationMode:0};

    public bool Start(IEnbSceneFrame scene,bool enabled,uint? destination,uint? gate,bool restricted,bool wormhole=false)
    {
        lock(sync)
        {
            if(phase!=Phase.Idle || sending || !CanStart(scene,enabled,destination,gate,restricted,wormhole))return false;
            this.destination=destination!.Value;this.gate=gate??0;sector=scene.Environment!.SectorNum;
            session=scene.ClientSoundStreamId;stop=new();phase=Phase.Select;StopReason=null;diagnostic?.Invoke("Autopilot started");return true;
        }
    }
    public void Tick(IEnbSceneFrame scene,bool enabled,uint? chosen,uint? resolvedGate,bool restricted,bool ready,
        Func<IReadOnlyList<uint>> buildWarpRoute,(int Ability,string Caster)? wormhole=null)
    {
        Action? command;
        lock(sync)
        {
            Prepare();command=dispatch;dispatch=null;
        }
        command?.Invoke(); // Never hold controller ownership while entering the session lock.
        void Prepare()
        {
            if(phase==Phase.Idle)return;
            if(!enabled || chosen!=destination || session!=scene.ClientSoundStreamId || restricted)
            {Cancel("Route or character changed");return;}
            if(scene.ActiveStarbaseId is not null || scene.Hud.Cockpit?.Warp?.Incapacitated==true)
            {Cancel("Flight unavailable");return;}
            if(scene.IsTransitioning || !ready)return;
            if(scene.Environment?.SectorNum is not uint current)return;
            if(current==destination){Cancel("Destination reached");return;}
            if(current!=sector)
            {
                sector=current;phase=Phase.Select;gate=resolvedGate??0;
            }
            if(phase==Phase.Gate)return; // The server/camera owns an accepted gate operation.
            // Next: a wormhole step. Its caster opens it and every client accepts; the sector change continues the route.
            if(phase==Phase.Wormhole)
            {if(!sending && Environment.TickCount64>wormholeDeadline)Cancel("The wormhole did not open");return;}
            if(wormhole is {} hole)
            {
                if(sending)return;
                if(Wormhole is not {} open){Cancel("Wormhole unavailable");return;}
                phase=Phase.Wormhole;wormholeDeadline=Environment.TickCount64+60000;
                Send(ct=>open(hole.Ability,hole.Caster,ct));return;
            }
            if(resolvedGate is not uint next || next==0 || (phase!=Phase.Select && next!=gate))
            {Cancel("Gate not recognised");return;}
            if(sending)return;
            gate=next;
            var ship=scene.Objects.FirstOrDefault(o=>o.IsLocalShip);
            if(ship is null){Cancel("Ship unavailable");return;}
            var selected=scene.Hud.HasLocalTargetSelection?scene.Hud.LocalSelectedTargetId:scene.Hud.CurrentTargetId;
            if(phase==Phase.Select)
            {phase=Phase.Selected;Send(ct=>target(gate,ct));return;}
            var status=scene.Hud.Cockpit?.Warp;
            if(status is null)return;
            if(phase==Phase.Warp && status.State is 4 or 5 or 6 or 7 or 9 or 10 or 11)
            {Cancel("Warp interrupted");return;}
            // Native warp updates select intermediate navs. They are not player input.
            // Once warp ends, select the resolved gate through the ordinary command.
            if(phase==Phase.Warp && status.IsEngaged)return;
            if(selected!=gate)
            {if(phase==Phase.Warp)Send(ct=>target(gate,ct));return;}
            if(!status.IsEngaged && scene.Hud.CurrentTargetVerbs.Contains(0x000A0000u))
            {phase=Phase.Gate;Send(ct=>verb(gate,0x000A0000u,ct));return;}
            if(phase==Phase.Selected && status.CanActivate && !status.IsEngaged && !status.CanRequestRescue && !status.CanTakeoff)
            {
                var route=buildWarpRoute();
                if(route.Count==0){Cancel("Warp route unavailable");return;}
                phase=Phase.Warp;Send(ct=>warp(ship.GameId,ship.RenderIdentity,route,ct));
            }
        }
    }
    private void Send(Func<CancellationToken,Task<bool>> command)
    {
        var owner=stop!;sending=true;
        dispatch=()=>{_=Complete();};
        async Task Complete()
        {
            bool success=false;string reason="Command refused";
            try {owner.Token.ThrowIfCancellationRequested();success=await command(owner.Token).ConfigureAwait(false);}
            catch(OperationCanceledException){reason="Cancelled";}
            catch(Exception ex){reason=ex.Message;}
            finally
            {
                lock(sync){sending=false;if(ReferenceEquals(owner,stop) && !success)Cancel(reason);}
            }
        }
    }
}
