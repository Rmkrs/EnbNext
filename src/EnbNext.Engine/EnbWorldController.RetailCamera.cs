using Point = EnbNext.Presentation.EnbUiPoint;
using Rect = EnbNext.Presentation.EnbUiRect;
using Size = EnbNext.Presentation.EnbUiSize;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldController
{
    private readonly EnbRetailCameraController _retailCamera = new();
    private readonly EnbRotatingCameraRuntime _rotatingCamera = new();
    private readonly EnbTakeoffCameraRuntime _takeoffCamera = new();
    private ulong? _retailCameraOwner;
    private bool _cameraHasBoundOwner;
    private ulong _cameraEventSequence, _cameraWarpSequence;
    private int? _processedCameraEvent;
    private Quaternion? _previousCameraOrientation;
    private ulong _takeoffEffectGeneration;
    public Func<ulong,ulong,Task<bool>>? TakeoffCameraCompletion { get; set; }

    private void ResetRetailCamera()
    {
        _retailCameraOwner = null;
        _shipEmotes.Clear(); _shipEmoteCamera.Reset(); _lootCamera.Reset();
        _warpCamera.Reset(); _gateCamera.Reset(); _dockCamera.Reset(); _landCamera.Reset();
        _rotatingCamera.Reset(); _takeoffCamera.Reset();
    }

    private EnbRetailCameraDecision PrepareRetailCamera(IEnbSceneFrame snapshot, IEnbRenderObject ship, uint now)
    {
        if (_retailCameraOwner != ship.RenderIdentity)
        {
            ResetRetailCamera();
            _retailCameraOwner = ship.RenderIdentity;
            _cameraEventSequence = _cameraWarpSequence = 0;
            _retailCamera.Reset(now, true, !_cameraHasBoundOwner);
            _cameraHasBoundOwner = true;
        }
        foreach (var item in snapshot.Camera.Events)
        {
            if (item.Sequence <= _cameraEventSequence) continue;
            _cameraEventSequence = item.Sequence;
            _retailCamera.Enqueue(item.Event);
        }
        if (ship.ShipPresentation is {} presentation)
            foreach (var change in presentation.WarpChanges)
            {
                if (change.Sequence <= _cameraWarpSequence) continue;
                _cameraWarpSequence = change.Sequence;
                if (change.State == 1) _retailCamera.Enqueue(2);
                else if (change.State == 2) _retailCamera.Enqueue(3);
                else if (change.State is 3 or 7 or 8 or 9 or 11) _retailCamera.Enqueue(4);
            }
        _shipEmotes.Observe(snapshot);
        if (_retailCamera.State == EnbRetailCameraState.Follow && _shipEmotes.HasPending(ship.RenderIdentity))
        {
            _shipEmotes.Resume(ship.RenderIdentity);
            _retailCamera.Enqueue(20);
        }
        var route = snapshot.Hud.WarpRoute;
        var destination = snapshot.Objects.FirstOrDefault(o=>o.GameId == route?.ReportedObjectId);
        bool final = route is not null && (route.ObjectIds.Length == 0 ? route.ReportedIndex is null :
            route.ReportedIndex == (uint)(route.ObjectIds.Length-1));
        float Radius(IEnbRenderObject obj) => _w3dWorld.ProjectedObjectBounds.DistanceRadiiByRenderIdentity.GetValueOrDefault(
            obj.RenderIdentity, EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(obj));
        bool nearWarp = final && destination?.Spatial is {} target &&
            EnbRetailCameraAdmission.NearWarpDestination(ship.Spatial!.Transform.Position,target.Transform.Position,Radius(destination));
        var loot = snapshot.Objects.FirstOrDefault(o=>o.GameId == snapshot.Camera.LootRequest?.TargetId);
        bool lootEligible = loot?.Spatial is {} lootPose && EnbRetailCameraAdmission.LootTargetEligible(ship.Spatial!.Transform.Position,
            lootPose.Transform.Position,Radius(loot));
        var decision = _retailCamera.Update(snapshot.Camera,now,true,snapshot.Objects.Any(o=>o.IsAttacking),nearWarp,lootEligible);
        _processedCameraEvent = decision.Event;
        if (decision.InterruptEmote) _shipEmotes.Interrupt(ship.RenderIdentity);
        if (decision.Changed)
        {
            // Stop paths retire only presentation providers; the authoritative physical pose is retained.
            _warpCamera.Reset(); _gateCamera.Reset(); _dockCamera.Reset(); _landCamera.Reset();
            _lootCamera.Reset(); _shipEmoteCamera.Reset(); _rotatingCamera.Reset(); _takeoffCamera.Reset();
            _landingEffectRequest = null;
            if (decision.State == EnbRetailCameraState.Rotating) _rotatingCamera.Start(ship.Spatial!.Transform,now);
            if (decision.State == EnbRetailCameraState.Takeoff) _takeoffEffectGeneration++;
        }
        if (decision.Action == 7 && snapshot.Camera.DockTransition is {} dock)
            _ = CompleteDockCameraAsync(ship.RenderIdentity,dock.Revision,dock.TargetId,dock.IsDeparture);
        if (decision.Action == 8 && snapshot.Camera.LandTransition is {} land)
            _ = CompleteLandCameraAsync(ship.RenderIdentity,land);
        if (decision.Action == 0x13 && snapshot.Camera.GateTransition is {} gate)
            _ = CompleteGateCameraAsync(ship.RenderIdentity,gate.Sequence,gate.TargetId,_gateCamera.CompletionDiagnostic);
        if (decision.Action == 0x1b && snapshot.Camera.TakeoffRevision is ulong revision)
            _ = CompleteTakeoffCameraAsync(ship.RenderIdentity,revision);
        if (decision.Event == 19 && snapshot.Camera.DockTransition is {IsDeparture:true} departure)
            _ = CompleteDockCameraAsync(ship.RenderIdentity,departure.Revision,departure.TargetId,true);
        if (decision.Event == 14 && snapshot.Camera.LandTransition is {Phase:Net7LandCameraPhase.SurfaceArrival} arrival)
            _ = CompleteLandCameraAsync(ship.RenderIdentity,arrival);
        return decision;
    }

    private EnbCameraPoseOverride? UpdateRetailCamera(EnbSceneFrame snapshot, IEnbRenderObject ship, uint now,
        out EnbSceneFrame emoteSnapshot)
    {
        var cameraDecision = PrepareRetailCamera(snapshot, ship, now);
        emoteSnapshot = _shipEmotes.Apply(snapshot.PrepareVisualFrame(), now, _retailCamera.State == EnbRetailCameraState.Distance);
        if (_retailCamera.State == EnbRetailCameraState.Distance && !_shipEmotes.IsRunning(ship.RenderIdentity))
            _retailCamera.Enqueue(21);
        var temporaryCamera = UpdateWarpCamera(snapshot, ship, now);
        var emoteObject = emoteSnapshot.Objects.First(o => o.RenderIdentity == ship.RenderIdentity);
        var emoteTransform = emoteObject.Spatial!.Transform;
        Vector3 emoteCenter = emoteTransform.Position;
        Vector3 physicalCenter = ship.Spatial!.Transform.Position;
        if (_w3dWorld.ProjectedObjectBounds.LocalSphereCenters.TryGetValue(ship.RenderIdentity, out var emoteCenterOffset))
        {
            emoteCenter = EnbRetailCameraTargetCenter.Evaluate(emoteTransform, emoteCenterOffset);
            physicalCenter = EnbRetailCameraTargetCenter.Evaluate(ship.Spatial!.Transform, emoteCenterOffset);
        }
        if (_retailCamera.State == EnbRetailCameraState.Loot)
        {
            temporaryCamera = UpdateLootCamera(snapshot, ship, physicalCenter, now, true);
            if (!_lootCamera.Active) _retailCamera.Enqueue(16);
        }
        temporaryCamera = _shipEmoteCamera.Update(emoteObject, _previousCameraPosition, emoteCenter,
            _retailCamera.State == EnbRetailCameraState.Distance) ?? temporaryCamera;
        temporaryCamera = UpdateAdditionalCamera(snapshot,ship,now) ?? temporaryCamera;
        if (cameraDecision.ForceSmooth)
            temporaryCamera = (temporaryCamera ?? new(null,null,false)) with { Snap=false,ForceSmooth=true };
        else if (cameraDecision.Changed && cameraDecision.State == EnbRetailCameraState.Follow)
            temporaryCamera = new(null,null,true);
        if (!snapshot.Camera.WarpCameraEnabled && ship.ShipPresentation?.GlobalWarpState == 2)
            temporaryCamera = (temporaryCamera ?? new(null,null,true)) with { Snap=true };
        return temporaryCamera;
    }

    private EnbCameraPoseOverride? UpdateAdditionalCamera(IEnbSceneFrame scene, IEnbRenderObject ship, uint now)
    {
        if (_retailCamera.State == EnbRetailCameraState.Wait && _previousCameraOrientation is {} orientation)
            return new(_previousCameraPosition,orientation,true,true);
        if (_retailCamera.State == EnbRetailCameraState.Rotating) return _rotatingCamera.Update(now);
        if (_retailCamera.State != EnbRetailCameraState.Takeoff) return null;
        if (!_takeoffCamera.Started)
        {
            var sweep = _w3dWorld.TakeoffSweep(ship.RenderIdentity,ship.Spatial!.Transform.Orientation);
            if (sweep is null) _takeoffCamera.StartUnavailable(now);
            else _takeoffCamera.Start(ship.Spatial.Transform,now,sweep);
        }
        var pose = _takeoffCamera.Update(now,_w3dWorld.ProjectedObjectBounds.LocalSphereCenters.GetValueOrDefault(ship.RenderIdentity),out bool complete);
        if (complete) _retailCamera.Enqueue(18);
        // A missing optional RenderObj must not move the view to an invented path.
        return pose ?? (_previousCameraOrientation is {} retained
            ? new EnbCameraPoseOverride(_previousCameraPosition,retained,true,true) : null);
    }

    private async Task CompleteTakeoffCameraAsync(ulong owner, ulong revision)
    {
        try { if (TakeoffCameraCompletion is {} callback) await callback(owner,revision); }
        catch (Exception ex) { _cameraControlStatus = "Takeoff camera completion failed: " + ex.Message; }
    }

    private EnbSceneFrame ApplyTakeoffCamera(EnbSceneFrame scene, ulong owner,int poseSlot)
    {
        if (_takeoffCamera.ShipTransform is not {} transform) return scene;
        scene.ApplyTransform(owner,transform);
        var ship=scene.Objects.First(o=>o.RenderIdentity==owner);
        var effects = scene.Effects;
        if (_takeoffCamera.Active)
        {
            uint id = uint.MaxValue;
            while (effects.Any(e=>e.EffectId == id)) id--;
            effects = effects.Add(new(id,_takeoffEffectGeneration,ship.GameId,ship.GameId,368,_takeoffCamera.StartTime,
                5000,"",0,false,1,default,0,0,false,default,1,EnbSceneEffectGeometryKind.Object,default,-1,default,0,0,null));
        }
        scene.Effects=effects;return scene;
    }
}
