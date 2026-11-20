using System.Numerics;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;
namespace EnbNext.Rendering;

/// <summary>Retail states 5 and 7: four-second Hermite reentry and surface arrival.</summary>
public sealed class EnbLandCameraRuntime(EnbRetailCameraRandomSource? random=null)
{
    private readonly EnbRetailCameraRandomSource _random=random??EnbRetailCameraRandomSource.Process;
    private (ulong,Net7LandCameraTransition)? _key;
    private Vector3 _start,_end,_startTangent,_endTangent,_upStart,_upEnd,_camera;
    private Matrix4x4 _cameraBasis;
    private float _cameraRate;
    private uint _started;
    private bool _ready,_completed;
    private readonly EnbRenderTransformState _pose=new();
    public IEnbRenderTransform? ShipTransform {get;private set;}
    public uint StartTime=>_started;
    public bool Active=>_ready && !_completed;
    public void Reset(){_key=null;_ready=_completed=false;ShipTransform=null;}

    public EnbCameraPoseOverride? Update(Net7LandCameraTransition? request,IEnbRenderObject ship,
        IEnbRenderObject? planet,float planetRadius,EnbRetailLandingPattern? pattern,
        Func<Vector2,float>? terrainHeight,uint now,bool enabled,out bool complete,Vector3 localShipCenter=default)
    {
        complete=false;
        if(request is null || ship.Spatial is null){Reset();return null;}
        var key=(ship.RenderIdentity,request);
        if(_key!=key)
        {
            Reset();_key=key;_started=now;
            if(enabled && request.Phase==Net7LandCameraPhase.SpaceApproach && planet?.Spatial is {} target)
            {
                var center=target.Transform.Position;
                var construction=EnbRetailSpaceLandingConstruction.Evaluate(ship.Spatial.Transform.Position,center,
                    planetRadius,_random.Next(0,1)!=0);
                _camera=construction.Camera;
                _cameraBasis=EnbRetailViewCamera.BetweenPoints(_camera,construction.CameraTarget);
                _cameraRate=_random.Next(0,1)!=0?.00002f:-.00002f;
                _start=construction.Start;_end=construction.End;
                var direction=construction.Direction;var turn=construction.Turn;
                (_startTangent,_endTangent)=EnbRetailSpaceLandingConstruction.Tangents(direction,turn);
                _upStart=Vector3.UnitZ;_upEnd=direction;
                _ready=true;
            }
            else if(enabled && request.Phase==Net7LandCameraPhase.SurfaceArrival && pattern is not null && terrainHeight is not null)
            {
                var transform=ship.Spatial.Transform;
                var physical=transform.ProviderTransform??EnbRetailCameraQuaternionBasis.Create(transform.Orientation);
                if(transform.ProviderTransform is null)physical.Translation=transform.Position;
                var construction=EnbRetailLandingConstruction.Evaluate(physical,pattern.StartOffset,pattern.SpeedForward,pattern.SpeedDown);
                _start=construction.Start;_end=construction.End;
                _startTangent=construction.StartTangent;_endTangent=construction.EndTangent;
                _upStart=pattern.InitialUp;_upEnd=construction.FinalUp;
                _camera=new Vector3(_random.Centered(500,_end.X),_random.Centered(500,_end.Y),_random.Centered(500,_end.Z));
                _camera.Z=MathF.Max(_camera.Z,terrainHeight(new(_camera.X,_camera.Y))+100);
                _cameraRate=0;_ready=true;
            }
        }
        uint elapsed=unchecked(now-_started);
        if(!_completed && (!_ready || elapsed>4000)){_completed=true;complete=true;}
        if(!_ready)return null;
        var sample=EnbRetailLandingSpline.Evaluate(_start,_end,_startTangent,_endTangent,
            EnbRetailLandingSpline.Fraction(elapsed));
        var position=sample.Position;
        var velocity=sample.Velocity;
        var matrix=EnbRetailLandingMatrix.Evaluate(position,velocity,_upStart,_upEnd,elapsed,1f/4000);
        var forward=new Vector3(matrix.M11,matrix.M12,matrix.M13);
        var rotation=EnbCurrentRetailParticleBirthTransform.ExtractOrientation(matrix);
        ShipTransform=_pose.Set(EnbRenderPose.FromTransform(ship.Spatial.Transform).Sample with {Position=position,Orientation=rotation,OrientationColumn0=forward,
            ProviderTransform=matrix,Velocity=velocity,Speed=velocity.Length()});
        // 7946D0 constructs 79C8E0; 794A90 selects world-up/player, and
        // 794970 delegates each surface-arrival frame to 79C970.
        if(request.Phase==Net7LandCameraPhase.SurfaceArrival)
            return EnbCameraPoseOverride.Point(_camera,EnbRetailCameraTargetCenter.Evaluate(matrix,localShipCenter),true,true);
        // 793FC0 retains 8C2780's matrix; 794063 selects rotating-provider axis 2.
        var orientation=EnbRetailViewCamera.Rotate(_cameraBasis,elapsed,_cameraRate,2);
        return EnbCameraPoseOverride.Native(_camera,orientation,true,true);
    }
}
