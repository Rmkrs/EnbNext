using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed record FrontendPrewarmRequest(ImmutableArray<EnbFrontendDraw> Draws);
    private FrontendPrewarmRequest? _frontendPrewarmRequest;
    private FrontendPrewarmRequest? _frontendPrewarming;
    private readonly HashSet<(string,int)> _frontendPrewarmKeys=[];
    private int _frontendPrewarmDrawIndex,_frontendPrewarmPassIndex;
    private long _frontendPrewarmMaxBatchMilliseconds;
    internal long FrontendPrewarmMaxBatchMilliseconds => Interlocked.Read(ref _frontendPrewarmMaxBatchMilliseconds);
    internal int FrontendPrewarmRemaining => _frontendPrewarming is {} request
        ? Math.Max(0,request.Draws.Length-_frontendPrewarmDrawIndex) : 0;

    public void QueueFrontendCreationPrewarm(ImmutableArray<EnbFrontendDraw> draws)
        => Volatile.Write(ref _frontendPrewarmRequest,new(draws));

    private void WarmFrontendCreation(EnbGlApi gl,EnbRetailAssetBundle assets)
    {
        var request=Volatile.Read(ref _frontendPrewarmRequest);
        if(request is null)
        {
            _frontendPrewarming=null;_frontendPrewarmKeys.Clear();return;
        }
        if(!ReferenceEquals(request,_frontendPrewarming))
        {
            _frontendPrewarming=request;
            _frontendPrewarmDrawIndex=_frontendPrewarmPassIndex=0;
            _frontendPrewarmKeys.Clear();
            foreach(var draw in request.Draws)
                foreach(var pass in draw.Mesh.MaterialPasses)
                    _frontendPrewarmKeys.Add((draw.Key,pass.Ordinal));
        }
        long started=Stopwatch.GetTimestamp();
        while(_frontendPrewarmDrawIndex<request.Draws.Length &&
            Stopwatch.GetElapsedTime(started).TotalMilliseconds<12)
        {
            var draw=request.Draws[_frontendPrewarmDrawIndex];
            var passes=draw.Mesh.MaterialPasses;
            if(_frontendPrewarmPassIndex>=passes.Count)
            { _frontendPrewarmDrawIndex++;_frontendPrewarmPassIndex=0;continue; }
            var pass=passes[_frontendPrewarmPassIndex++];
            var key=(draw.Key,pass.Ordinal);
            if(_frontendGpu.ContainsKey(key))continue;
            try
            {
                var (lane,material,atlas)=CompileFrontendShipPrewarm(draw,pass,assets);
                if(!TryCreateGpuResource(gl,lane,LiveLaneKind.Textured,0,out var gpu,out var error))
                    throw new InvalidDataException(error);
                _frontendGpu.Add(key,new(gpu,draw.Mesh));
                _frontendClassShipMaterials[key]=(pass,material,atlas);
            }
            catch
            {
                // The visible creator frame retains the strict render path and
                // will report a genuine asset error. Prewarming is opportunistic.
                _frontendPrewarming=null;
                Volatile.Write(ref _frontendPrewarmRequest,null);
                _frontendPrewarmKeys.Clear();
                return;
            }
        }
        long elapsed=(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long previous;
        do
        {
            previous=Interlocked.Read(ref _frontendPrewarmMaxBatchMilliseconds);
            if(elapsed<=previous)break;
        }while(Interlocked.CompareExchange(ref _frontendPrewarmMaxBatchMilliseconds,elapsed,previous)!=previous);
    }

    private (LiveLanePlan Lane,EnbW3dDrawMaterialBinding Material,EnbCurrentRetailEnvironmentAtlasSet? Atlas)
        CompileFrontendShipPrewarm(EnbFrontendDraw draw,
        EnbW3dRenderMaterialPass pass,EnbRetailAssetBundle assets)
    {
        var packet=new EnbW3dDrawTask(new(EnbRenderPassKind.ModernOverlayBoundary,
            EnbW3dDrawBlendClass.Blended,draw.Mesh.StaticSort,draw.Resource.Key,
            draw.Mesh.Ordinal,pass.Ordinal,1,0),1,1,0,draw.Transform,
            new(draw.Transform.Translation,draw.Mesh.BoundingRadius),draw.Mesh,null,
            new(pass,pass.SingleBindingSemantics),EnbW3dLightEnvironment.BlackNoLights,
            draw.Time,null,0,draw.Mesh.Name);
        Vector3 Direction(Vector3 p)=>draw.Camera.Basis.Right*p.X+
            draw.Camera.Basis.Up*p.Y-draw.Camera.Basis.Forward*p.Z;
        packet=(packet).ForkTask().SetLightEnvironment(EnbW3dLightEnvironment.CreateCurrentRetail(0,[
            new(true,new(EnbW3dLightType.Directional,new(3),Vector3.Zero,Vector3.Zero,
                Vector3.Zero,Direction(new(1,-1,-1)),1000000,1,1,0,0,0,0)),
            new(true,new(EnbW3dLightType.Directional,new Vector3(.06f,.42f,.83f)*1.3f,
                Vector3.Zero,Vector3.Zero,Vector3.Zero,Direction(new(-1,1,1)),1000000,1,1,0,0,0,0))]));
        EnbCurrentRetailEnvironmentAtlasSet? atlas=null;
        if(draw.ShipColors is {} colors &&
            assets.EnvironmentMaps?.TryResolveReflection(100000,out var reflection)==true)
        {
            atlas=ResolveEnvironmentAtlasSet(assets,assets.EnvironmentMaps,reflection,0,out _);
            packet=EnbCurrentRetailShipColorizationRuntime.Apply(packet,colors,
                draw.ShipColorRegion,reflection.Axis,out _);
        }
        if(draw.ShipDecals is {} decals)
            packet=EnbCurrentRetailShipDecalRuntime.Apply(packet,decals,assets.Decals,out _,_swatchCatalog);
        if(!EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out var error) ||
            !TryCompileLane(submission,draw.Camera,EnbRenderTextureRuntimeSettings.RetailDefaults,
                EnbRenderTextureBackendCapabilities.ModernDefault,assets.Textures,
                new Dictionary<string,EnbTextureImage>(),atlas,LiveLaneKind.Textured,out var lane,out error))
            throw new InvalidDataException("Creator ship prewarm: "+error);
        return (lane,packet.Material,atlas);
    }
}
