using EnbNext.Client;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;
internal sealed partial class EnbWorldController
{
    internal EnbSectorAutopilot? Autopilot {get;set;}
    /// <summary>Next: auto pilot waits (for example for the group to form up again after a wormhole).</summary>
    internal Func<bool>? AutopilotHold {get;set;}
    internal void SetAutopilot(bool start)
    {
        if(Autopilot is not {} pilot)return;
        if(!start){pilot.Cancel("Autopilot button");_ui.Cockpit.StopAutopilotWarp();return;}
        if(pilot.Active)return;
        var scene=_snapshotAccessor();
        if(scene is null || _awaitingWorld || _ui.Layout.Editing)return;
        var guidance=_ui.Map.Guidance;
        bool enabled=_retailAssetsAccessor()?.CharacterInterface.AutoPilotAvailable==true;
        if(pilot.Start(scene,enabled,guidance.Destination,guidance.Gate,!string.IsNullOrEmpty(guidance.RestrictionNotice),guidance.NextWormhole is not null))
            FlightInputSuspended?.Invoke();
    }
    private void UpdateAutopilot(IEnbSceneFrame scene)
    {
        var pilot=Autopilot;
        bool enabled=_retailAssetsAccessor()?.CharacterInterface.AutoPilotAvailable==true;
        var guidance=_ui.Map.Guidance;
        bool restricted=!string.IsNullOrEmpty(guidance.RestrictionNotice);
        if(pilot?.Active==true)
            pilot.Tick(scene,enabled,guidance.Destination,guidance.Gate,restricted,!_awaitingWorld&&AutopilotHold?.Invoke()!=true,
                ()=>EnbNavigationRoutePlanner.Build(scene,guidance.Gate??0),
                guidance.NextWormhole is {} wormhole?(wormhole.AbilityId,wormhole.Caster):null);
        _ui.Cockpit.UpdateAutopilot(enabled,pilot?.Active==true,!_awaitingWorld && EnbSectorAutopilot.CanStart(scene,enabled,guidance.Destination,guidance.Gate,restricted,guidance.NextWormhole is not null));
        _hudGpu.SetOptionsPresentation(_ui.Cockpit.RoundButtons(_ui.Options.Presentation));
    }
}
