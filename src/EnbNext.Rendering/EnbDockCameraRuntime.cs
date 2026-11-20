using System.Numerics;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;
namespace EnbNext.Rendering;

public sealed record EnbDockCameraAnchor(ulong TargetOwner, EnbRetailDockCameraDefinition Definition, Matrix4x4 BoneWorld);

/// <summary>00792750/00792590: authored dock approach and timed completion.</summary>
public sealed class EnbDockCameraRuntime(EnbRetailCameraRandomSource? random = null)
{
    private readonly EnbRetailCameraRandomSource _random = random ?? EnbRetailCameraRandomSource.Process;
    private (ulong,ulong,uint,bool)? _key;
    private Vector3 _start, _camera;
    private EnbRetailLinearMotion? _motion;
    private float _speed, _duration;
    private bool _ready, _completed;
    private uint _started;
    private readonly EnbRenderTransformState _pose=new();
    public IEnbRenderTransform? ShipTransform { get; private set; }
    public void Reset() { _key=null; _ready=_completed=false; ShipTransform=null; _motion=null; }
    public EnbCameraPoseOverride? Update(Net7DockCameraTransition? request, IEnbRenderObject ship,
        EnbDockCameraAnchor? anchor, uint now, out bool complete, Vector3 localShipCenter = default, bool targetExists = false)
    {
        complete=false;
        if(request is null || ship.Spatial is null) {Reset();return null;}
        var key=(ship.RenderIdentity,request.Revision,request.TargetId,request.IsDeparture);
        if(_key!=key)
        {
            Reset();_key=key;_started=now;
            if(anchor is not null)
            {
                var d=anchor.Definition;
                var end=Vector3.Transform(d.EndOffset,anchor.BoneWorld);
                _start=end+Vector3.TransformNormal(d.StartOffset,anchor.BoneWorld);
                if(request.IsDeparture) (_start,end)=(end,_start);
                _motion=new(_start,end,d.Speed,now);
                _speed=d.Speed;_duration=d.DurationMilliseconds;
                float angle=_random.Scale(d.AngleRadians);
                if(_random.Next(0,1)!=0)angle=MathF.PI-angle;
                _camera=EnbRetailDockCameraPlacement.Evaluate(_start,end,request.IsDeparture?d.ExitLead:d.EnterLead,angle,d.Radius,75);
                _ready=true;
            }
            else if(targetExists)
            {
                // 00792750 draws for valid targets even without Bone metadata;
                // 00792590 completes immediately because +14 remains clear.
                _random.Scale(0);
                _random.Next(0,1);
            }
        }
        if(!_completed && (!_ready || unchecked(now-_started)>_duration))
        { _completed=true;complete=true; }
        if(!_ready)return null;
        var sample=_motion!.At(now);
        var matrix=sample.Transform;
        ShipTransform=_pose.Set(EnbRenderPose.FromTransform(ship.Spatial.Transform).Sample with {ClientTime=now,Position=sample.Position,
            Orientation=EnbCurrentRetailParticleBirthTransform.ExtractOrientation(matrix),ProviderTransform=matrix,
            Velocity=sample.Velocity,Speed=_speed,Rotation=Vector3.Zero,
            OrientationColumn0=new(matrix.M11,matrix.M12,matrix.M13)});
        var center=EnbRetailCameraTargetCenter.Evaluate(matrix,localShipCenter);
        return EnbCameraPoseOverride.Point(_camera,center,true,true);
    }
}
