using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private EnbStationBeamEffect? _stationArrivalBeam, _stationDepartureBeam;
    private readonly string?[] _stationBeamFailures=new string?[2];
    private string StationBeamWarnings=>string.Join("",_stationBeamFailures.Where(f=>f is not null).Select(f=>" · teleport unavailable: "+f));
    private readonly Dictionary<(bool Departure,string Mesh,int Pass),(LiveLanePlan Lane,LiveGpuResource Gpu)> _stationBeamGpu=[];
    private readonly Dictionary<(bool Departure,string Mesh,int Pass),EnbNext.Rendering.OpenGL.EnbOpenGlTextureMeshRecoveryState> _stationBeamRecoveryState=[];
    private void DestroyStationBeam(EnbGlApi? gl,bool preserveScene=false)
    {
        if(preserveScene)
        {foreach(var (key,draw) in _stationBeamGpu)_stationBeamRecoveryState[key]=RetainGpuState(draw.Gpu);}
        else _stationBeamRecoveryState.Clear();
        foreach(var draw in _stationBeamGpu.Values)draw.Gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);
        _stationBeamGpu.Clear();
    }
    private void RenderStationBeam(EnbGlApi gl,int fb,int width,int height,EnbCurrentRetailStationInterior interior,long elapsed)
    {
        int direction=_stationDepartureStarted is null?0:1;
        if(_stationBeamFailures[direction] is not null)return;
        try {RenderStationBeamCore(gl,fb,width,height,interior,elapsed);}
        catch(Exception error) when(error is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            //7896B0 releases a failed optional effect locally. Keep rendering
            //the station, its people and the usable avatar after that failure.
            _stationBeamFailures[direction]=error.Message;
            DestroyStationBeam(gl);
        }
    }
    private void RenderStationBeamCore(EnbGlApi gl,int fb,int width,int height,EnbCurrentRetailStationInterior interior,long elapsed)
    {
        if(!interior.PlayDocking && _stationDepartureStarted is null)return;
        long time=_stationDepartureStarted is {} start ? elapsed-start : elapsed-(long)Math.Ceiling(interior.ArrivalDurationMilliseconds);
        if(time<0 || _stationMovement is null)return;
        var assets=_retailAssetsAccessor()!;
        var effect=_stationDepartureStarted is null ? _stationArrivalBeam ??=new(assets,false) : _stationDepartureBeam ??=new(assets,true);
        if(time>effect.TailDurationMilliseconds){DestroyStationBeam(gl);return;}
        var camera=StationCamera(interior,width,height);
        foreach(var packet in effect.Build(time,_stationMovement.Transform,camera))
        {
            if(!EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out var error))throw new InvalidDataException(error);
            bool particle=packet.Mesh.Name.EndsWith(".ParticleBuffer",StringComparison.Ordinal);
            var key=(_stationDepartureStarted is not null,packet.Mesh.Name,packet.Material.Source.Ordinal);
            LiveLanePlan lane;LiveGpuResource gpu;
            if(!particle && _stationBeamGpu.TryGetValue(key,out var cached))
            {
                // Visibility changes reorder the aggregate binding ordinals; retain mapper state while refreshing geometry.
                if(!EnbW3dProgrammableTextureLaneCompiler.TryUpdateFrame(cached.Lane.Textured!,submission,camera,out var updated,out error,geometryWillBeUploaded:true))throw new InvalidDataException("Beam frame "+packet.Mesh.Name+": "+error);
                lane=LiveLanePlan.From(updated);gpu=LiveGpuResource.From(_texturedBackend.UpdateGeometry(gl,cached.Gpu.Textured!,updated));
                _stationBeamGpu[key]=(lane,gpu);
            }
            else
            {
                if(!TryCompileLane(submission,camera,EnbRenderTextureRuntimeSettings.RetailDefaults,EnbRenderTextureBackendCapabilities.ModernDefault,
                    assets.Textures,new Dictionary<string,EnbNext.Assets.EnbTextureImage>(),null,LiveLaneKind.Textured,out lane,out error) ||
                    !TryCreateGpuResource(gl,lane,LiveLaneKind.Textured,0,out gpu,out error,_stationBeamRecoveryState.GetValueOrDefault(key)))throw new InvalidDataException("Station beam: "+error);
                _stationBeamRecoveryState.Remove(key);
                if(!particle)_stationBeamGpu[key]=(lane,gpu);
            }
            try { SubmitGpuResource(gl,fb,width,height,gpu,lane,new(false,0,0,0),default,null,unchecked((uint)elapsed)); }
            finally { if(particle)gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend); }
        }
    }
}
