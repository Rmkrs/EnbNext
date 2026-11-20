using System.Numerics;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>795570 setup and 79C590 module: fixed translation, rotating basis.</summary>
public sealed class EnbRotatingCameraRuntime(Func<int, int, int>? random = null)
{
    private readonly Func<int, int, int> _random = random ?? EnbRetailCameraRandomSource.Process.Next;
    private Vector3 _position;
    private Matrix4x4 _basis;
    private uint _started;
    private float _rate;
    private int _axis;
    public bool Active { get; private set; }
    public void Reset() => Active = false;

    public void Start(IEnbRenderTransform ship, uint now)
    {
        float side = _random(0, 1) == 0 ? 1 : -1;
        _position = ship.Position + Vector3.Transform(new Vector3(.5f, side * 1.75f, 0) * 75, ship.Orientation);
        _basis = EnbRetailViewCamera.BetweenPoints(_position, ship.Position);
        _rate = _random(0, 1) == 0 ? -0.000005f : 0.000005f;
        _axis = _random(0, 2);
        _started = now;
        Active = true;
    }

    public EnbCameraPoseOverride? Update(uint now)
    {
        if (!Active) return null;
        var orientation = EnbRetailViewCamera.Rotate(_basis,unchecked(now-_started),_rate,_axis);
        return EnbCameraPoseOverride.Native(_position,orientation,false,true);
    }
}
