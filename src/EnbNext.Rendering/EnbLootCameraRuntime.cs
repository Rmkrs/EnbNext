using System.Numerics;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Retail tractor camera: 00797490, 00797570 and 00797770.</summary>
public sealed class EnbLootCameraRuntime(EnbRetailCameraRandomSource? random = null)
{
    private readonly EnbRetailCameraRandomSource _random = random ?? EnbRetailCameraRandomSource.Process;
    private ulong _owner, _sequence;
    private uint _target, _started, _returnStarted;
    private int _phase;
    private Vector3 _position;
    public bool Active => _phase is 1 or 2;
    public void Reset() { _owner = _sequence = 0; _phase = 0; }

    public EnbCameraPoseOverride? Update(Net7LootCameraRequest? request,
        IEnbRenderObject ship, IEnbRenderObject? target, Vector3 targetCenter,
        Vector3 shipCenter, float targetRadius, uint now, bool enabled, bool available)
    {
        if (_owner != ship.RenderIdentity)
        {
            bool replaced = _owner != 0;
            Reset(); _owner = ship.RenderIdentity;
            if (replaced) { _sequence = request?.Sequence ?? 0; return null; }
        }
        if (request is null) { _phase = 0; return null; }
        if (request.Sequence != _sequence)
        {
            _sequence = request.Sequence;
            // Events that arrive while a different camera owns the view are consumed.
            if (enabled && available && _phase != 2 && ship.Spatial is not null && target?.Spatial is not null &&
                (_phase != 1 || request.TargetId != _target))
            {
                _target = request.TargetId; _started = now; _phase = 1;
                float angle = _random.LootAngle();
                if (_random.Next(0,1) != 0) angle = MathF.PI - angle;
                _position = EnbRetailLootCameraPlacement.Evaluate(ship.Spatial.Transform.Position,
                    target.Spatial.Transform.Position,angle,75);
            }
        }
        if (!available || ship.Spatial is null) { _phase = 0; return null; }
        if (_phase == 1)
        {
            if (unchecked(now - _started) > 15000) _phase = 3;
            else if (target?.Spatial is null || EnbRetailCameraAdmission.ReturnFromLoot(ship.Spatial.Transform.Position,
                target.Spatial.Transform.Position,targetRadius))
            { _phase = 2; _returnStarted = now; }
        }
        if (_phase == 2 && unchecked(now - _returnStarted) > 1000) _phase = 3;
        return Active ? EnbCameraPoseOverride.Point(_position,
            _phase == 1 ? targetCenter : shipCenter,false,true) : null;
    }
}
