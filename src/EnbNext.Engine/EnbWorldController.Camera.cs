using Point = EnbNext.Presentation.EnbUiPoint;
using Rect = EnbNext.Presentation.EnbUiRect;
using Size = EnbNext.Presentation.EnbUiSize;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;
using System.Numerics;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldController
{
    public Action<uint,string>? CameraMessage { get; set; }
    private string _cameraControlStatus = "camera controls ready";
    private readonly EnbWarpCameraRuntime _warpCamera = new();
    private readonly EnbGateCameraRuntime _gateCamera = new();
    private readonly EnbDockCameraRuntime _dockCamera = new();
    public Func<ulong,ulong,uint,bool,Task<bool>>? DockCameraCompletion { get; set; }
    private async Task CompleteDockCameraAsync(ulong owner, ulong revision, uint target, bool departure)
    {
        try
        {
            bool sent=DockCameraCompletion is {} complete && await complete(owner,revision,target,departure);
            _cameraControlStatus=sent?"Dock camera completed.":"Dock camera completion was not sent.";
        }
        catch(Exception ex){_cameraControlStatus="Dock camera completion failed: "+ex.Message;}
    }
    private readonly EnbLootCameraRuntime _lootCamera = new();

    private EnbCameraPoseOverride? UpdateLootCamera(IEnbSceneFrame snapshot, IEnbRenderObject ship,
        Vector3 shipCenter, uint now, bool available)
    {
        var target = snapshot.Objects.FirstOrDefault(o => o.GameId == snapshot.Camera.LootRequest?.TargetId);
        Vector3 center = target?.Spatial?.Transform.Position ?? default;
        var bounds = _w3dWorld.ProjectedObjectBounds;
        if (target?.Spatial is {} spatial && bounds.LocalSphereCenters.TryGetValue(target.RenderIdentity, out var offset))
            center = EnbRetailCameraTargetCenter.Evaluate(spatial.Transform, offset);
        float radius = target is null ? 0 : bounds.DistanceRadiiByRenderIdentity.GetValueOrDefault(
            target.RenderIdentity, EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(target));
        return _lootCamera.Update(snapshot.Camera.LootRequest, ship, target, center, shipCenter, radius,
            now, snapshot.Camera.LootCameraEnabled, available);
    }
    private readonly EnbShipEmoteRuntime _shipEmotes = new();
    private readonly EnbShipEmoteCamera _shipEmoteCamera = new();
    public Func<ulong, ulong, uint, Task<bool>>? GateCameraCompletion { get; set; }
    private Vector3 _previousCameraPosition;
    private string? _savedCameraPath;
    private CameraStartupKey? _savedCameraStartupKey;
    // ClientGraphicsSettingsCtor 0075A280 initializes embedded +28 (SClient+1178).
    // This is transient client state, separate from character Bridge_Mode.
    private bool _bridgeVisible = true;
    public bool BridgeVisible => _bridgeVisible;
    private readonly EnbBridgeCameraLook _bridgeLook = new();
    private readonly EnbBridgeModelMotion _bridgeMotion = new();
    private readonly EnbBridgeTransitionRuntime _bridgeTransition = new();
    private EnbRetailBridgeResource? _bridgeAsset;
    private EnbRetailAssetBundle? _bridgeAssets;
    private ulong? _bridgeOwner;
    private uint? _bridgeRace;
    private bool _bridgeBound;
    public Action<double>? SampleCameraInput { get; set; }
    public bool ApplyCameraPointer(Point normalized,double elapsed,bool initial,bool distance,bool control)
    {
        if(RecordedViewInput.Recorder is not null)RecordedViewInput.Observe(new("pointer",normalized.X,normalized.Y,elapsed,initial,distance,control));
        var snapshot=_snapshotAccessor();
        if(!CanControlFlight || snapshot is null)return false;
        EnsureCameraController(snapshot.Camera);
        if(_cameraController.IsFirstPerson)
        {
            if(!control || !_bridgeBound)return false;
            _bridgeLook.TryAdjust(initial?0:normalized.X-.5,initial?0:normalized.Y-.5,
                initial?0:elapsed,control,false,_bridgeBound,_bridgeTransition.RequestedState,_bridgeTransition.CompletedState);
            //50ABC0 ignores the setter's state rejection and still captures/
            //recenters;53CA00/53C980 alone gate look while a panel is selected.
            return true;
        }
        _manualCameraInput=true;
        return initial?_cameraController.BeginManualCameraInput(snapshot.Camera,distance):
            _cameraController.AdjustCamera(snapshot.Camera,normalized.X-.5,normalized.Y-.5,elapsed,distance);
    }
    private bool _manualCameraInput;
    public void EndManualCameraInput()
    {
        if(_manualCameraInput && RecordedViewInput.Recorder is not null)RecordedViewInput.Observe(new("end"));
        _manualCameraInput = false;
        _cameraController.EndManualCameraInput();
    }

    private bool TryRequestBridgePanel(int state)
    {
        if (!_stationActive && _bridgeAsset is { } asset)
        {
            // A hidden bridge cannot hold utility input behind its animation.
            bool animate = _cameraController.IsFirstPerson && _bridgeVisible &&
                _snapshotAccessor()?.Camera.BridgeCameraEnabled == true;
            if (!animate && _bridgeTransition.IsTransitioning) _bridgeTransition.Reset();
            if (_bridgeTransition.IsTransitioning) return false;
            if (state != _bridgeTransition.RequestedState)
                _bridgeTransition.TryRequest(asset, state, animate);
        }
        if (state is 1 or 2) { _ui.Map.CloseForPopup(); _ui.GalaxyMap.CloseForPopup(); }
        else if (state == 3) _ui.Inventory.CloseUtilityPanels();
        return true;
    }

    private EnbResolvedRenderCamera UpdateBridgeCamera(IEnbRenderObject ship,
        EnbResolvedRenderCamera camera, EnbCameraPoseOverride? temporary, double elapsed, double width, double height)
    {
        var assets = _retailAssetsAccessor();
        var race = ship.Avatar?.Race;
        bool replaced = _bridgeOwner != ship.RenderIdentity || _bridgeRace != race || !ReferenceEquals(_bridgeAssets, assets);
        if (replaced)
        {
            _bridgeOwner = ship.RenderIdentity; _bridgeRace = race; _bridgeAssets = assets;
            _bridgeAsset = null; _bridgeLook.Reset(); _bridgeMotion.Reset(); _bridgeTransition.Reset();
        }
        bool cameraBridge = _snapshotAccessor()?.Camera.BridgeCameraEnabled == true;
        if (assets is not null && race is not null)
        {
            _bridgeAsset ??= EnbRetailBridgeResource.Load(assets, unchecked((int)race.Value));
            // The utility views can also be retired by loading or a modal. Their
            // current visibility is authoritative after an in-flight transition.
            int panel = _ui.Map.IsOpen || _ui.GalaxyMap.IsOpen ? 3 : _ui.Inventory.CharacterOpen ? 2 : _ui.Inventory.InventoryOpen ? 1 : 0;
            bool animate = _cameraController.IsFirstPerson && _bridgeVisible &&
                temporary?.Position is null && temporary?.Orientation is null && cameraBridge;
            if (!animate && _bridgeTransition.IsTransitioning) _bridgeTransition.Reset();
            if (!_bridgeTransition.IsTransitioning && panel != _bridgeTransition.RequestedState)
                _bridgeTransition.TryRequest(_bridgeAsset, panel, !replaced && animate);
            _bridgeTransition.Advance(elapsed);
        }
        // 00744C10 requires the normal player chase camera to be active.
        // A snap-only warp override still uses that camera; fixed cinematic poses do not.
        _bridgeBound = _cameraController.IsFirstPerson && _bridgeVisible && assets is not null && race is not null &&
            temporary?.Position is null && temporary?.Orientation is null;
        if (!_bridgeBound)
        {
            _w3dWorld.SetBridgeFrame(null);
            return camera;
        }
        var animation = _bridgeTransition.Sample;
        var local = _bridgeLook.ConsumeRetailTransform(_bridgeAsset!.CameraTransform(
            _bridgeTransition.CameraAttachment(cameraBridge), animation), _bridgeTransition.RequestedState);
        var composition = EnbBridgeCameraComposition.Create(EnbBridgeCameraComposition.RetailTransform(camera), local);
        var avatarSteering = _bridgeMotion.SmoothedAngularRate;
        var modelTransform = _bridgeMotion.Update(ship.Spatial!.Transform.Rotation, _bridgeTransition.RequestedState);
        _w3dWorld.SetBridgeFrame(new(_bridgeAsset, modelTransform, composition) { Animation = animation, AvatarSteeringRate = avatarSteering });
        return EnbBridgeCameraComposition.ResolveCamera(composition.WorldCamera, camera, camera.Projection.ClipRange);
    }

    private EnbCameraPoseOverride? UpdateWarpCamera(IEnbSceneFrame snapshot, IEnbRenderObject ship, uint now)
    {
        if(_retailCamera.State is EnbRetailCameraState.Landing or EnbRetailCameraState.Arrival)
        {
            _warpCamera.Reset();_gateCamera.Reset();_dockCamera.Reset();
            return UpdateLandCamera(snapshot,ship,now);
        }
        _landCamera.Reset();
        if(_retailCamera.State == EnbRetailCameraState.Dock && snapshot.Camera.DockTransition is {} dock)
        {
            _warpCamera.Reset();_gateCamera.Reset();
            var target=snapshot.Objects.FirstOrDefault(o=>o.GameId==dock.TargetId);
            var anchor=_w3dWorld.DockAnchors.GetValueOrDefault(dock.TargetId);
            if(anchor?.TargetOwner!=target?.RenderIdentity)anchor=null;
            if(dock.IsDeparture && !snapshot.Camera.DockCameraEnabled)anchor=null;
            var pose=_dockCamera.Update(dock,ship,anchor,now,out bool complete,
                _w3dWorld.ProjectedObjectBounds.LocalSphereCenters.GetValueOrDefault(ship.RenderIdentity), target is not null && !(dock.IsDeparture && !snapshot.Camera.DockCameraEnabled));
            if(complete)_retailCamera.Enqueue(dock.IsDeparture ? 19 : 11);
            return pose;
        }
        _dockCamera.Reset();
        if (_retailCamera.State == EnbRetailCameraState.Gate && snapshot.Camera.GateTransition is {} request)
        {
            _warpCamera.Reset();
            var gate = snapshot.Objects.FirstOrDefault(o => o.GameId == request.TargetId);
            float gateRadius = gate is null ? 0 : EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(gate);
            Vector3 localShipCenter=_w3dWorld.ProjectedObjectBounds.LocalSphereCenters.GetValueOrDefault(ship.RenderIdentity);
            var pose = _gateCamera.Update(request, ship, gate, gateRadius, localShipCenter, now, out bool commit);
            if (commit) _retailCamera.Enqueue(24);
            return pose;
        }
        _gateCamera.Reset();
        if (_retailCamera.State != EnbRetailCameraState.Warp) { _warpCamera.Reset(); return null; }
        var route = snapshot.Hud.WarpRoute;
        var destination = snapshot.Objects.FirstOrDefault(o => o.GameId == route?.ReportedObjectId);
        bool final = route is not null && (route.ObjectIds.Length == 0 ? route.ReportedIndex is null :
            route.ReportedIndex == (uint)(route.ObjectIds.Length - 1));
        var bounds = _w3dWorld.ProjectedObjectBounds;
        float radius = destination is null ? 0 : bounds.DistanceRadiiByRenderIdentity.GetValueOrDefault(
            destination.RenderIdentity, EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(destination));
        Vector3 position = ship.Spatial!.Transform.Position;
        Vector3 center = bounds.LocalSphereCenters.TryGetValue(ship.RenderIdentity, out var localCenter)
            ? EnbRetailCameraTargetCenter.Evaluate(ship.Spatial.Transform, localCenter) : position;
        var warpPose = _warpCamera.Update(ship, now, snapshot.Camera.WarpCameraEnabled,
            destination, final, radius, center, _previousCameraPosition, true, _processedCameraEvent);
        if (_warpCamera.Phase == "normal") _retailCamera.Enqueue(5);
        return warpPose;
    }
    private async Task CompleteGateCameraAsync(ulong owner, ulong sequence, uint target, string? timing)
    {
        try
        {
            bool sent = GateCameraCompletion is {} complete && await complete(owner, sequence, target);
            _cameraControlStatus = (sent ? "Gate camera completed. " : "Gate camera completion was not sent. ") + timing;
        }
        catch (Exception ex) { _cameraControlStatus = "Gate camera completion failed: " + ex.Message; }
    }
    public bool ZoomCameraWheel(bool forward)
    {
        if(RecordedViewInput.Recorder is not null)RecordedViewInput.Observe(new("wheel",Initial:forward));
        var snapshot=_snapshotAccessor();
        if(!CanControlFlight || snapshot is null)return false;
        EnsureCameraController(snapshot.Camera);
        if(!_cameraController.ZoomCameraWheel(snapshot.Camera,forward))return false;
        _manualCameraInput=true;
        return true;
    }

    public bool HandleCameraAction(string? action, uint? session)
    {
        if(RecordedViewInput.Recorder is not null)RecordedViewInput.Observe(new("action",Action:action));
        if (action is not ("Restore Camera" or "Save Camera" or "First Person" or "Next Camera" or "Prev Camera")) return false;
        var snapshot = _snapshotAccessor();
        void Message(string text)
        {
            _cameraControlStatus = text;
            if (session is uint character && character != 0) CameraMessage?.Invoke(character, text);
        }
        if (CanUseGeneralControls && _stationActive && action == "Save Camera")
        { Message("Cannot save camera in starbase."); return true; }
        if(_stationActive && action=="Restore Camera")
        {
            if(CanControlStation)_w3dWorld.RestoreStationCamera();
            return true;
        }
        if (_stationActive && action=="First Person")
        {
            if(!CanControlStation)return true;
            _w3dWorld.StationThirdPersonMode=!_w3dWorld.StationThirdPersonMode;
            if(session is uint character && character!=0 && _retailAssetsAccessor() is {} assets)
                try {assets.SaveStarbaseThirdPersonMode(character,_w3dWorld.StationThirdPersonMode);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
                {Message("Could not save station camera mode: "+ex.Message);}
            return true;
        }
        if (!CanControlFlight || snapshot is null) return true;
        EnsureCameraController(snapshot.Camera);
        switch (action)
        {
            case "Restore Camera": _cameraController.RestoreCamera(); break;
            case "First Person":
                bool changed = _cameraController.IsFirstPerson
                    ? _cameraController.RestoreDefaultThirdPerson(snapshot.Camera)
                    : _cameraController.SelectFirstPerson(snapshot.Camera);
                if (changed) Message(_cameraController.IsFirstPerson ? "Camera set to 1st person mode." :
                    $"Camera set to 3rd person mode {_cameraController.ActiveModeIndex},");
                if (changed && session is uint cameraOwner && cameraOwner != 0 && _retailAssetsAccessor() is {} cameraAssets)
                {
                    try
                    {
                        cameraAssets.SaveBridgeMode(cameraOwner, _cameraController.IsFirstPerson);
                        var options = cameraAssets.ResolvePlayerOptions(cameraOwner);
                        if (_cameraStartupKey is {} currentKey)
                            _savedCameraStartupKey = currentKey with { PlayerOptionsSelector = options.Selector,
                                PlayerOptionsSourcePath = options.Provenance?.Path, StartInFirstPerson = options.Options.BridgeMode };
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    { _cameraControlStatus = "Camera mode changed, but saving Bridge_Mode failed: " + ex.Message; }
                }
                break;
            case "Next Camera":
                if (_cameraController.IsFirstPerson) { ToggleBridgeVisibility(); break; }
                if (_cameraController.SelectNextThirdPersonMode(snapshot.Camera)) Message($"Camera set to 3rd person mode {_cameraController.ActiveModeIndex}");
                break;
            case "Prev Camera":
                if (_cameraController.IsFirstPerson) { ToggleBridgeVisibility(); break; }
                if (_cameraController.SelectPreviousThirdPersonMode(snapshot.Camera)) Message($"Camera set to 3rd person mode {_cameraController.ActiveModeIndex}");
                break;
            case "Save Camera":
                if (_cameraController.IsFirstPerson) { Message("Cannot save camera in first person mode."); break; }
                if (session is not uint id || _retailAssetsAccessor() is not {} assets ||
                    _cameraController.GetCurrentMode(snapshot.Camera) is not {} mode) break;
                try
                {
                    assets.SaveCameraMode(id, new(mode.Index, mode.HeadingRadians, mode.Distance,
                        mode.TiltRadians, mode.HorizontalFieldOfViewRadians, mode.ElevationOffsetRadians));
                    _cameraController.RememberSavedMode(mode);
                    _savedCameraPath = assets.ResolveCameraModes(id)?.Provenance.Path;
                    // 00743D18–53 passes the stored floats directly (angles remain radians).
                    Message(FormattableString.Invariant($"Camera Slot {mode.Index} Saved: H{mode.HeadingRadians:F2}, T:{mode.TiltRadians:F2}, D:{mode.Distance:F2} FOV:{mode.HorizontalFieldOfViewRadians:F2} EO:{mode.ElevationOffsetRadians:F2}"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                { Message($"Failed to save Camera Slot {mode.Index}!"); _cameraControlStatus += " " + ex.Message; }
                break;
        }
        return true;

        void ToggleBridgeVisibility()
        {
            _bridgeVisible = !_bridgeVisible;
            _cameraController.RestoreCamera();
            Message(_bridgeVisible ? "Camera set to 1st person mode with bridge." :
                "Camera set to 1st person mode without bridge.");
        }
    }
}
