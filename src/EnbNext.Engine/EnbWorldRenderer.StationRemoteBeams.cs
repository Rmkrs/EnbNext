using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed record RemoteBeam(EnbStationRemotePopulation.Actor Actor,EnbStationBeamEffect Effect,Matrix4x4 World,uint Started);
    private EnbRetailTeleportScene _stationRemoteTeleportScene=new();
    internal Func<EnbNext.Assets.EnbRetailAssetBundle,bool,EnbStationBeamEffect> StationRemoteTeleportFactory {get;set;}=static(assets,departing)=>new(assets,departing);
    private readonly List<RemoteBeam> _stationRemoteBeams=[];
    private readonly Dictionary<(EnbStationBeamEffect Owner,string Mesh,int Pass),(LiveLanePlan Lane,LiveGpuResource Gpu)> _stationRemoteBeamGpu=[];
    private readonly Dictionary<(EnbStationBeamEffect Owner,string Mesh,int Pass),EnbNext.Rendering.OpenGL.EnbOpenGlTextureMeshRecoveryState> _stationRemoteBeamRecovery=[];
    private void DestroyStationRemoteBeams(EnbGlApi? gl,bool preserveScene)
    {
        foreach(var (key,draw) in _stationRemoteBeamGpu)
        {
            if(preserveScene)_stationRemoteBeamRecovery[key]=RetainGpuState(draw.Gpu);
            draw.Gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);
        }
        _stationRemoteBeamGpu.Clear();
        if(!preserveScene)
        {_stationRemoteBeams.Clear();_stationRemoteTeleportScene=new();_stationRemoteBeamRecovery.Clear();}
    }
    private void BeginStationRemoteTeleport(EnbStationRemotePopulation.Actor actor,StationPerson person,uint clock,long elapsed)
    {
        if(actor.RequestedTeleport!=0)
        {
            foreach(var previous in _stationRemoteBeams.Where(b=>ReferenceEquals(b.Actor,actor)))previous.Effect.Destroy(clock);
            EnbStationBeamEffect? effect=null;
            try
            {
                effect=StationRemoteTeleportFactory(_retailAssetsAccessor()!,actor.RequestedTeleport==3);
                var world=actor.Motion.Transform;
                world.Translation=person.Collision!.Move(world.Translation,Vector3.Zero);
                //7896B0 attaches at the current actor transform before its update.
                //9C5980 captures the current BFAE3C at binding, not the
                //previous frame's clock. The actor countdown still ticks later.
                effect.Prepare(_stationRemoteTeleportScene,clock,0,world,(cue,position)=>QueueStationAnimationSound(actor.Pose.AvatarId,cue,position,elapsed));
                _stationRemoteBeams.Add(new(actor,effect,world,clock));
                person.TeleportDiagnostic=null;
            }
            catch(Exception error) when(error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                effect?.Destroy(clock);actor.Teleport.Clear();person.TeleportDiagnostic=error.Message;
            }
            actor.RequestedTeleport=0;
        }
    }
    private void RenderStationRemoteBeams(EnbGlApi gl,int fb,int width,int height,EnbCurrentRetailStationInterior room,uint clock,uint delta,long elapsed)
    {
        var retained=_stationPopulation.Ordered.ToHashSet();
        foreach(var beam in _stationRemoteBeams)
        {
            try
            {
                if(!retained.Contains(beam.Actor))beam.Effect.Destroy(clock);
                beam.Effect.Prepare(_stationRemoteTeleportScene,clock,unchecked(clock-beam.Started),beam.World,
                    (cue,position)=>QueueStationAnimationSound(beam.Actor.Pose.AvatarId,cue,position,elapsed));
            }
            catch(Exception error) when(error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                beam.Effect.Destroy(clock);beam.Actor.Teleport.Clear();
                if(_stationRemotePeople.TryGetValue(beam.Actor.Pose.AvatarId,out var person))person.TeleportDiagnostic=error.Message;
            }
        }
        _stationRemoteTeleportScene.Advance(clock,delta,error=>
        {
            foreach(var beam in _stationRemoteBeams)
                if(_stationRemotePeople.TryGetValue(beam.Actor.Pose.AvatarId,out var person))person.TeleportDiagnostic=error.Message;
        });
        var camera=StationCamera(room,width,height);
        var active=new HashSet<(EnbStationBeamEffect Owner,string Mesh,int Pass)>();
        foreach(var beam in _stationRemoteBeams)
        {
            try
            {
                foreach(var packet in beam.Effect.Draw(clock,camera))
                {
                    if(!EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out var error))throw new InvalidDataException(error);
                    bool particle=packet.Mesh.Name.EndsWith(".ParticleBuffer",StringComparison.Ordinal);
                    var key=(beam.Effect,packet.Mesh.Name,packet.Material.Source.Ordinal);
                    LiveLanePlan lane;LiveGpuResource gpu;
                    if(!particle && _stationRemoteBeamGpu.TryGetValue(key,out var cached))
                    {
                        if(!EnbW3dProgrammableTextureLaneCompiler.TryUpdateFrame(cached.Lane.Textured!,submission,camera,out var updated,out error,geometryWillBeUploaded:true))throw new InvalidDataException(error);
                        lane=LiveLanePlan.From(updated);gpu=LiveGpuResource.From(_texturedBackend.UpdateGeometry(gl,cached.Gpu.Textured!,updated));
                        _stationRemoteBeamGpu[key]=(lane,gpu);
                    }
                    else
                    {
                        if(!TryCompileLane(submission,camera,EnbRenderTextureRuntimeSettings.RetailDefaults,EnbRenderTextureBackendCapabilities.ModernDefault,
                            _retailAssetsAccessor()!.Textures,new Dictionary<string,EnbNext.Assets.EnbTextureImage>(),null,LiveLaneKind.Textured,out lane,out error) ||
                            !TryCreateGpuResource(gl,lane,LiveLaneKind.Textured,0,out gpu,out error,_stationRemoteBeamRecovery.GetValueOrDefault(key)))throw new InvalidDataException(error);
                        _stationRemoteBeamRecovery.Remove(key);
                        if(!particle)_stationRemoteBeamGpu[key]=(lane,gpu);
                    }
                    active.Add(key);
                    try{SubmitGpuResource(gl,fb,width,height,gpu,lane,new(false,0,0,0),default,null,clock);}
                    finally{if(particle)gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);}
                }
            }
            catch(Exception error) when(error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                beam.Effect.Destroy(clock);beam.Actor.Teleport.Clear();
                if(_stationRemotePeople.TryGetValue(beam.Actor.Pose.AvatarId,out var person))person.TeleportDiagnostic=error.Message;
            }
        }
        _stationRemoteTeleportScene.CompleteRender(clock);
        _stationRemoteBeams.RemoveAll(b=>b.Effect.Retired);
        foreach(var key in _stationRemoteBeamGpu.Keys.Where(k=>!active.Contains(k)).ToArray())
        {_stationRemoteBeamGpu[key].Gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);_stationRemoteBeamGpu.Remove(key);}
    }
}
