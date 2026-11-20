using System.Numerics;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public readonly record struct EnbCameraPoseOverride(Vector3? Position, Quaternion? Orientation, bool Snap,
    bool ForceThirdPerson = false, bool ForceSmooth = false)
{
    public Quaternion? RetailCameraOrientation { get; init; }
    public static EnbCameraPoseOverride Point(Vector3 position,Vector3 target,bool snap,bool forceThirdPerson=false)
    {
        var native=EnbRetailPointCameraOrientation.Evaluate(position,target).Orientation;
        return Native(position,native,snap,forceThirdPerson);
    }
    public static EnbCameraPoseOverride Native(Vector3 position,Quaternion native,bool snap,bool forceThirdPerson=false)
    {
        return new(position,EnbRetailPointCameraOrientation.CanonicalOrientation(native),snap,forceThirdPerson)
            {RetailCameraOrientation=native};
    }
}

/// <summary>00797B10/00797B50: temporary fixed departure/approach views,
/// interleaved with the player's normal chase camera. No flight authority.</summary>
public sealed class EnbWarpCameraRuntime
{
    private readonly EnbRetailCameraRandomSource _random;
    private ulong? _owner;
    private ulong _sequence;
    private int _stage = -1;
    private uint _deadline, _travelStart;
    private Vector3 _fixedPosition;
    public string Phase => _stage switch { 0 => "departure setup", 1 => "departure", 2 => "travel",
        3 => "waypoint approach", 4 => "destination approach", _ => "normal" };
    public EnbWarpCameraRuntime(EnbRetailCameraRandomSource? random = null) => _random = random ?? EnbRetailCameraRandomSource.Process;
    public void Reset() { _owner = null; _stage = -1; _sequence = 0; }

    public EnbCameraPoseOverride? Update(IEnbRenderObject ship, uint now, bool enabled,
        IEnbRenderObject? destination, bool finalDestination, float destinationRadius,
        Vector3 renderedShipCenter, Vector3 previousCameraPosition, bool controllerManaged = false, int? controllerEvent = null)
    {
        if (ship.Spatial?.Transform is not {} transform || ship.ShipPresentation is not {} presentation)
        { Reset(); return null; }
        if (controllerManaged)
            presentation = presentation with { WarpChanges = controllerEvent is 2 or 3 or 4
                ? [new(_sequence + 1, controllerEvent.Value == 2 ? 1 : controllerEvent.Value == 3 ? 2 : 3, 0, 0)] : [] };
        if (_owner != ship.RenderIdentity)
        {
            Reset(); _owner = ship.RenderIdentity;
            _sequence = presentation.WarpChanges.Length > 1 ? presentation.WarpChanges[^2].Sequence : 0;
        }
        bool entered = false, exited = false;
        foreach (var change in presentation.WarpChanges)
        {
            if (change.Sequence <= _sequence) continue;
            _sequence = change.Sequence;
            if (change.State == 1 && enabled && _stage < 0)
            {
                // 00799E40 suppresses a cinematic start near the final destination.
                if (finalDestination && destination?.Spatial?.Transform is {} target &&
                    EnbRetailCameraAdmission.NearWarpDestination(transform.Position,target.Position,destinationRadius)) continue;
                _stage = 0; _deadline = now + 2000;
                Vector3 direction = DestinationDirection(transform, destination);
                var angles = _random.WarpDepartureAngles();
                _fixedPosition = EnbRetailWarpCameraPlacement.Departure(transform.Position,direction,
                    angles.First,angles.Opposite,75,previousCameraPosition);
            }
            else if (change.State == 2) entered = true;
            else if (change.State is 3 or 7 or 8 or 9 or 11) exited = true;
        }
        if (!enabled)
        {
            _stage = -1;
            int? state = presentation.WarpChanges.LastOrDefault()?.State ?? presentation.GlobalWarpState;
            return state == 2 ? new(null, null, true) : null;
        }
        if (_stage < 0) return null;
        if (exited)
        {
            if (_stage == 4) _deadline = now + 2000;
            else { _stage = -1; return null; }
        }
        switch (_stage)
        {
            case 0 when entered || now > _deadline:
                _stage = 1; _deadline = now + 6000; break;
            case 1 or 3 when now > _deadline:
                _stage = 2; _deadline = now + 5000; _travelStart = now; break;
            case 2 when now > _deadline && now - _travelStart > 3000 && destination?.Spatial?.Transform is {} target:
                if (EnbRetailCameraAdmission.CanStartWarpApproach(transform.Position,target.Position,
                    transform.Velocity,destinationRadius,finalDestination,out float distance,out float rawSpeed))
                {
                    float angle = _random.Centered(.62831855f) + (_random.Next(0,1) != 0 ? MathF.PI : 0);
                    _stage = finalDestination ? 4 : 3;
                    _deadline = finalDestination ? uint.MaxValue : now + 4000;
                    _fixedPosition = EnbRetailWarpCameraPlacement.Approach(transform.Position,
                        finalDestination ? DestinationDirection(transform, destination) : ShipForward(transform),
                        finalDestination ? rawSpeed : distance, angle, 75, finalDestination);
                }
                break;
            case 4 when now > _deadline: _stage = -1; return null;
        }
        if (_stage == 2) return new(null, null, true);
        // A fixed exterior view must also expose the ship when the user's
        // remembered normal camera is the cockpit. Travel resumes that mode.
        return EnbCameraPoseOverride.Point(_fixedPosition,renderedShipCenter,_stage!=0,true);
    }

    private static Vector3 DestinationDirection(IEnbRenderTransform ship, IEnbRenderObject? target)
        => target?.Spatial?.Transform is {} spatial ? spatial.Position - ship.Position : ShipForward(ship);

    private static Vector3 ShipForward(IEnbRenderTransform ship)
    {
        //6F4610 returns the provider matrix; 6F3FA0 only adjusts columns1/2.
        var basis=ship.ProviderTransform??EnbRetailCameraQuaternionBasis.Create(ship.Orientation);
        return new(basis.M11,basis.M12,basis.M13);
    }

}
