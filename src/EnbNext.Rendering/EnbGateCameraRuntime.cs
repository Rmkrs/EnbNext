using System.Numerics;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>00795A50/00795A80/00795C80: server-authorized gate approach,
/// opposite-side commit view, then normal camera after sector replacement.</summary>
public sealed class EnbGateCameraRuntime(EnbRetailCameraRandomSource? random = null)
{
    private readonly EnbRetailCameraRandomSource _random = random ?? EnbRetailCameraRandomSource.Process;
    private ulong _owner, _sequence;
    private int _quadrant;
    private bool _committed, _positionReady;
    private Vector3 _position;
    private EnbRetailLinearMotion? _motion;
    private float? _startDistance;
    public string? CompletionDiagnostic { get; private set; }
    private readonly EnbRenderTransformState _pose=new();
    public IEnbRenderTransform? ShipTransform { get; private set; }
    public string Phase => _sequence == 0 ? "normal" : _committed ? "gate transition" : "gate approach";
    public void Reset() { _owner = _sequence = 0; _committed = _positionReady = false; ShipTransform = null;
        _motion=null; _startDistance = null; CompletionDiagnostic = null; }

    public EnbCameraPoseOverride? Update(Net7GateCameraTransition? request,
        IEnbRenderObject ship, IEnbRenderObject? gate, float radius,
        Vector3 localShipCenter, uint now, out bool commit)
    {
        commit = false;
        if (request is null || ship.Spatial is null) { Reset(); return null; }
        var position = ship.Spatial.Transform.Position;
        Vector3 shipCenter=EnbRetailCameraTargetCenter.Evaluate(ship.Spatial.Transform,localShipCenter);
        bool validGate = gate is { ObjectType: 11, Spatial: not null };
        if (_owner != ship.RenderIdentity || _sequence != request.Sequence)
        {
            Reset(); _owner = ship.RenderIdentity; _sequence = request.Sequence;
            if (validGate)
            {
                float angle = _random.Scale(MathF.PI / 4);
                _quadrant = _random.GateQuadrant();
                angle = _quadrant switch { 1 => MathF.PI - angle, 2 => angle + MathF.PI, 3 => -angle, _ => angle };
                _position = EnbRetailGateCameraPlacement.Approach(position,gate!.Spatial!.Transform.Position,angle,75);
                _positionReady = true;

                Vector3 delta = gate.Spatial.Transform.Position - position;
                _startDistance = delta.Length();
                _motion=new(position,gate!.Spatial!.Transform.Position,4,now);
            }
        }
        if (_positionReady)
        {
            // 00795C80 installs 00718160 with speed 4. 00718410 advances
            // origin + elapsed client milliseconds * velocity, without clamping.
            var sample=_motion!.At(now);var matrix=sample.Transform;
            shipCenter=EnbRetailCameraTargetCenter.Evaluate(matrix,localShipCenter);
            position=sample.Position;
            ShipTransform=_pose.Set(EnbRenderPose.FromTransform(ship.Spatial.Transform).Sample with { ClientTime=now,Position = position,
                Orientation = EnbCurrentRetailParticleBirthTransform.ExtractOrientation(matrix),ProviderTransform=matrix,
                Velocity = sample.Velocity, Speed = 4,Rotation=Vector3.Zero,
                OrientationColumn0=new(matrix.M11,matrix.M12,matrix.M13) });
        }
        uint elapsed = unchecked(now - request.StartClientTime);
        float? distance = validGate ? Vector3.Distance(position, gate!.Spatial!.Transform.Position) : null;
        float threshold = (radius + 75) * .5f;
        bool near = distance is float d && d < threshold;
        if (!_committed && (elapsed > 7000 || near))
        {
            CompletionDiagnostic = FormattableString.Invariant(
                $"gate 0x{request.TargetId:X8}: {(near ? "proximity" : "timeout")} after {elapsed}ms; start distance {_startDistance:0.##}; end distance {distance:0.##}; threshold {threshold:0.##}; approach 4000u/s");
            _committed = true;
            commit = true;
            if (validGate)
            {
                float angle = _random.Scale(MathF.PI / 4);
                angle = _quadrant switch { 0 => MathF.PI - angle, 2 => -angle, 3 => angle + MathF.PI, _ => angle };
                var transform = gate!.Spatial!.Transform;
                //6F4610 returns the provider matrix; type11 skips6F3FA0's
                //ship-only adjustment.6F3C60's render-object repair is later.
                var gateMatrix=transform.ProviderTransform??EnbRetailCameraQuaternionBasis.Create(transform.Orientation);
                if(transform.ProviderTransform is null)gateMatrix.Translation=transform.Position;
                _position=EnbRetailGateCameraPlacement.Commit(gateMatrix,angle,75);
                _positionReady = true;

                _motion=new(position,gate!.Spatial!.Transform.Position,4,now);
            }
        }
        return _positionReady ? EnbCameraPoseOverride.Point(_position,shipCenter,true) : null;
    }
}
