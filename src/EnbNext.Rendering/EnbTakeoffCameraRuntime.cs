using System.Numerics;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>796920 / 796440 / 796850. A temporary history provider, never flight input.</summary>
public sealed class EnbTakeoffCameraRuntime(EnbRetailCameraRandomSource? random = null)
{
    private readonly EnbRetailCameraRandomSource _random = random ?? EnbRetailCameraRandomSource.Process;
    private IEnbRenderTransform? _physical;
    private readonly EnbRenderTransformState _physicalStorage=new(),_pose=new();
    private Vector3 _middle, _end, _camera;
    private Quaternion _startOrientation,_endOrientation;
    private uint _started;
    private uint _previousTime;
    private bool _relocated, _completed;
    public IEnbRenderTransform? ShipTransform { get; private set; }
    public bool Active => _physical is not null && !_completed;
    public bool Started { get; private set; }
    public uint StartTime => _started;
    public void Reset() { _physical = ShipTransform = null; _relocated = _completed = Started = false; }
    // 796300 retains its timer even when 796920 cannot acquire the ship RenderObj.
    public void StartUnavailable(uint now) { Reset(); _started = now; Started = true; }

    public void Start(IEnbRenderTransform ship, uint now, Func<Vector3, Vector3, Vector3, float> sweep)
    {
        Reset(); _physical = ship; _started = _previousTime = now; Started = true;
        var initialMatrix=ship.ProviderTransform??EnbRetailCameraQuaternionBasis.Create(ship.Orientation);
        if(ship.ProviderTransform is null)initialMatrix.Translation=ship.Position;
        _physical=_physicalStorage.Set(EnbRenderPose.FromTransform(ship).Sample with {ProviderTransform=initialMatrix});
        _startOrientation=EnbCurrentRetailParticleBirthTransform.ExtractOrientation(initialMatrix);
        _middle = new Vector3(ship.Position.X, ship.Position.Y, _random.Range(500,100,ship.Position.Z));
        _end = _middle + Vector3.UnitZ * 5000;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            float elevation = _random.Degrees(30,30);
            float yaw = _random.Degrees(315,45);
            var candidate=EnbRetailTakeoffMotion.Candidate(initialMatrix,_middle,elevation,yaw);
            if (sweep(_middle, candidate.Position, candidate.Direction * 1000000) >= 1) { _end = candidate.Position; break; }
        }
        //796920: object look-at8C29B0 followed by8D2710, not point-camera79C970.
        _endOrientation = EnbCurrentRetailParticleBirthTransform.ExtractOrientation(
            EnbRetailCameraLookAt.BetweenPoints(_middle, _end));
        var midpoint = (ship.Position + _middle) * .5f;
        float angle = _random.Degrees(360);
        _camera = EnbRetailTakeoffCameraPlacement.Evaluate(EnbRetailCameraLookAt.BetweenPoints(midpoint,_middle),angle,75,0,false);
        ShipTransform = _physical;
    }

    public EnbCameraPoseOverride? Update(uint now, Vector3 localCenter, out bool complete)
    {
        complete = false;
        if (_physical is not {} physical)
        {
            if (Started && !_completed && unchecked(now-_started)>=5000) { _completed=true; complete=true; }
            return null;
        }
        // Point module samples the previous history pose before this frame writes it.
        var tracked = ShipTransform ?? physical;
        var trackedCenter = EnbRetailCameraTargetCenter.Evaluate(tracked,localCenter);
        var pose = EnbCameraPoseOverride.Point(_camera,
            trackedCenter, true, true);
        uint elapsed = unchecked(now - _started);
        if (elapsed >= 5000)
        {
            if (!_completed) { _completed = true; complete = true; }
            return pose;
        }
        if (elapsed >= 2000)
        {
            if (!_relocated)
            {
                _relocated = true;
                float angle = _random.CenteredDegrees(30);
                if (_random.Next(0,1) != 0) angle = MathF.PI - angle;
                _camera = EnbRetailTakeoffCameraPlacement.Evaluate(EnbRetailCameraLookAt.BetweenPoints(_middle,_end),
                    angle,75,_random.TakeoffAhead(75),true);
            }
        }
        var sample=EnbRetailTakeoffMotion.Sample(physical.Position,_middle,_end,_startOrientation,_endOrientation,elapsed);
        var matrix=sample.Transform;
        var position=matrix.Translation;
        var orientation=sample.Orientation;
        uint delta = unchecked(now - _previousTime);
        _previousTime = now;
        var previous = ShipTransform ?? physical;
        var previousMatrix=previous.ProviderTransform??EnbRetailCameraQuaternionBasis.Create(previous.Orientation);
        Vector3 velocity=EnbRetailTransformHistory.Velocity(position,previous.Position,delta);
        Vector3 rotation=EnbRetailTransformHistory.AngularVelocity(matrix,previousMatrix,delta);
        ShipTransform=_pose.Set(EnbRenderPose.FromTransform(physical).Sample with { ClientTime = now, Position = position, Orientation = orientation,
            ProviderTransform=matrix,OrientationColumn0 = new(matrix.M11,matrix.M12,matrix.M13),
            Velocity = velocity, Speed = velocity.Length(), Rotation = rotation });
        return pose;
    }

}
