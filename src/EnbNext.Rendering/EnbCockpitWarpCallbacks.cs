using System.Collections.Immutable;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>005821C0 callback gate, with the retained 13D/13E/150 cockpit fields.</summary>
public sealed class EnbCockpitWarpCallbacks
{
    private EnbSceneShipPresentationSnapshot? _presentationSource,_presentationOutput;
    private ulong? owner;
    private Guid stream;
    private Net7CockpitWarpAuxState? previous;
    private bool incapacitated, refresh, available;
    private bool enabled = true; // ConstructGadgetBase initializes enabled bit 20.
    private ulong sequence;
    private EnbSceneCockpitWarpCommand command = new(0, 0, null);
    private ImmutableArray<EnbSceneWarpPresentationChange> callbacks = [];

    public EnbSceneFrame ApplyFrame(EnbSceneFrame scene, EnbRetailCameraState camera, bool active = true)
    {
        var ship = scene.Objects.FirstOrDefault(o => o.IsLocalShip);
        if (owner != ship?.RenderIdentity || stream != scene.ClientSoundStreamId)
        {
            owner = ship?.RenderIdentity; stream = scene.ClientSoundStreamId;
            previous = null; incapacitated = refresh = available = false;
            enabled = true; sequence = 0; callbacks = []; command = new(0, 0, null);
        }
        var status = scene.Hud.Cockpit?.Warp;
        if (ship?.ShipPresentation is not {} presentation || status?.AuxCallbacks is not {} aux) return scene;
        // Aux normalization continues while the space cockpit is inactive.
        // Do not replay its missed dirty-property callbacks on returning.
        if (!active) { previous = aux; return Publish(); }

        bool Dirty(ulong revision, ulong old, int state) => state > 1 && revision != old;
        bool speedDirty = Dirty(aux.MaximumSpeedRevision, previous?.MaximumSpeedRevision ?? 0, aux.MaximumSpeedState);
        bool lockDirty = Dirty(aux.LockSpeedRevision, previous?.LockSpeedRevision ?? 0, aux.LockSpeedState);
        if (lockDirty || speedDirty)
        {
            bool hasEngine = aux.MaximumSpeedState != 0 && status.MaximumSpeed is {} maximum && maximum != 0;
            enabled = hasEngine && !status.SpeedLocked;
            if (hasEngine) refresh = true;
        }
        bool lockedCamera = camera is EnbRetailCameraState.Landing or EnbRetailCameraState.Dock
            or EnbRetailCameraState.Arrival or EnbRetailCameraState.Takeoff;
        if (lockedCamera)
        {
            if (enabled) { enabled = false; refresh = true; }
        }
        else if (incapacitated)
        {
            if (!refresh) { enabled = true; refresh = true; }
        }
        else if (status.PresentationMode is 1 or 5) enabled = false;
        else
        {
            if (refresh ? aux.AvailabilityState != 0 : Dirty(aux.AvailabilityRevision, previous?.AvailabilityRevision ?? 0, aux.AvailabilityState))
                enabled = available = status.Availability == 2;
            if (refresh ? aux.PrivateWarpState != 0 : Dirty(aux.PrivateWarpRevision, previous?.PrivateWarpRevision ?? 0, aux.PrivateWarpState))
            {
                int state = status.State ?? 0;
                callbacks = callbacks.Add(new(++sequence, state, 0, 0));
                command = new(sequence, state, status.TriggerTime, aux.TriggerTimeState != 0);
                if (state == 0) enabled = available;
                else if (state is 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11) enabled = false;
            }
            refresh = false;
        }
        // 581670 observes incapacitation after 5821C0; recovery does not
        // clear 13E, so the next normal frame refreshes retained properties.
        incapacitated = status.Incapacitated;
        previous = aux;
        return Publish();

        EnbSceneFrame Publish()
        {
            var poses=scene.RenderPoses;
            poses.WritePrivateWarp(ship.RenderPoseSlot,callbacks);
            if(scene.Hud.Cockpit is EnbCockpitFrame cockpit)cockpit.WarpCommand=command;
            if(!ReferenceEquals(_presentationSource,presentation) || _presentationOutput?.PrivateWarpChanges!=callbacks)
            {_presentationSource=presentation;_presentationOutput=presentation with {PrivateWarpChanges=callbacks};}
            ((EnbRenderObjectState)ship).ShipPresentation=_presentationOutput;
            return scene;
        }
    }
}
