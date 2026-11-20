using EnbNext.Client;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>One flight intent and one in-flight send; the host supplies pointer transport only.</summary>
internal sealed class EnbWorldFlightCommands<TKey>(Func<Net7FlightInput,Task<bool>>? send,
    Action releasePointer,Action<int> setThrust) where TKey:notnull
{
    internal EnbFlightInputState<TKey> Input {get;}=new();
    internal bool WarpSending {get;private set;}
    private bool sending;

    internal async Task PumpAsync()
    {
        if(sending || send is null || WarpSending)return;
        sending=true;
        try
        {
            Net7FlightInput sent;
            do {sent=Input.Intent;await send(sent);}
            while(!WarpSending && sent!=Input.Intent);
        }
        catch(Exception ex) when(ex is InvalidOperationException or OperationCanceledException or IOException)
        {Input.ResetAfterSendFailure();releasePointer();}
        finally {sending=false;}
    }

    internal async Task RequestWarpAsync(IEnbSceneFrame? scene,bool canUseCockpit,
        Func<uint,ulong,IReadOnlyList<uint>,Task<bool>>? activate)
    {
        if(WarpSending || !canUseCockpit || activate is null || scene is not {IsTransitioning:false} ||
            scene.Hud.Cockpit?.Warp is not {} status || !(status.CanActivate || status.CanRequestRescue || status.CanTakeoff))return;
        var ship=scene.Objects.FirstOrDefault(o=>o.IsLocalShip);
        if(ship is null)return;
        uint? target=scene.Hud.HasLocalTargetSelection?scene.Hud.LocalSelectedTargetId:scene.Hud.CurrentTargetId;
        var route=!status.CanRequestRescue && !status.CanTakeoff && target is >0 and uint id?EnbNavigationRoutePlanner.Build(scene,id):[];
        // ProcessWarpButtonInput 00583250 -> IsWarpPathReady 007BD980 rebuilds the
        // route (007B7EF0) and sends StartWarp with whatever it holds; a route that
        // could not be built is sent empty, the same packet as a free warp.
        WarpSending=true;
        Input.Suspend();releasePointer();setThrust(0);
        try {await activate(ship.GameId,ship.RenderIdentity,route);}
        catch(Exception ex) when(ex is InvalidOperationException or OperationCanceledException or IOException)
        { /* Retired route: never replay. */ }
        finally {WarpSending=false;}
    }
}
