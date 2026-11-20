using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed record ScannerPresentation(EnbUiRect ModelBounds, EnbUiRect PanelBounds);
    public void SetScannerPresentation(EnbUiRect modelBounds, EnbUiRect panelBounds)
        => Volatile.Write(ref _scannerPresentation, new(modelBounds, panelBounds));

    private EnbGaugeDraw? _gaugePresentation;
    private EnbAuthoredTargetPanel? _gaugeAsset;
    private EnbAuthoredUiAsset? _interruptGaugeAsset;
    private float _interruptTrail,_activationTrail;
    private ulong _interruptGeneration;
    public string? InterruptionGaugeFailure {get;private set;}
    private readonly EnbOpenGlScannerFramebuffer _gaugeFramebuffer = new();
    private readonly List<(EnbW3dOverlayDrawOwner Owner, LiveGpuResource Gpu, int Kind, bool Interruption)> _gaugeDraws = [];
    private (int Width, int Height) _gaugeSize;
    private float _hullTrail, _shieldTrail;
    private readonly EnbRetailGaugeTrail _hullGauge=new(),_shieldGauge=new(),_interruptGauge=new(),_activationGauge=new();
    private uint _gaugeTime;
    private uint? _gaugeTargetId;

    public void SetGaugePresentation(EnbGaugeDraw? presentation)
        => Volatile.Write(ref _gaugePresentation, presentation);

    private void DestroyGaugeResources(EnbGlApi? gl)
    {
        foreach (var draw in _gaugeDraws)
            draw.Gpu.Destroy(gl, _unlitBackend, _litBackend, _texturedBackend);
        _gaugeDraws.Clear();
        _gaugeAsset = null;
        _interruptGaugeAsset=null;
    }

    private void RenderGauges(EnbGlApi gl, int fb, int width, int height, double scaling,
        EnbUiRect bounds, uint time)
        => RenderGaugeLayer(gl,fb,width,height,scaling,bounds,time,false);

    private void RenderGaugeLayer(EnbGlApi gl, int fb, int width, int height, double scaling,
        EnbUiRect bounds, uint time, bool interruptionOnly)
    {
        if (Volatile.Read(ref _gaugePresentation) is not { } presentation ||
            _retailAssetsAccessor() is not { } assets) return;
        if(interruptionOnly && presentation.Interruption is null)return;
        int x = (int)Math.Round(bounds.X * scaling), y = height - (int)Math.Round(bounds.Bottom * scaling);
        int w = (int)Math.Round(bounds.Width * scaling), h = (int)Math.Round(bounds.Height * scaling);
        if (w < 1 || h < 1 || x < 0 || y < 0 || x+w > width || y+h > height) return;
        if(!ReferenceEquals(_gaugeAsset,presentation.Asset) || _gaugeTargetId!=presentation.TargetId)
        {
            _hullGauge.Reset(presentation.Hull);_shieldGauge.Reset(presentation.Shield);
            _hullTrail=presentation.Hull; _shieldTrail=presentation.Shield; _gaugeTime=time;
            _gaugeTargetId=presentation.TargetId;
        }
        uint elapsed=unchecked(time-_gaugeTime);
        if(presentation.Interruption is {} interruption)
        {
            if(_interruptGaugeAsset is null || _interruptGeneration!=interruption.Generation)
            {_interruptGauge.Reset(0);_activationGauge.Reset(0);_interruptTrail=_activationTrail=0;_interruptGeneration=interruption.Generation;}
            _interruptTrail=_interruptGauge.Update(interruption.Progress,elapsed);
            _activationTrail=_activationGauge.Update(interruption.Activation,elapsed);
        }
        _hullTrail=_hullGauge.Update(presentation.Hull,elapsed);
        _shieldTrail=_shieldGauge.Update(presentation.Shield,elapsed);
        _gaugeTime=time;
        if (!ReferenceEquals(_gaugeAsset, presentation.Asset) || _gaugeSize != (w,h) ||
            !ReferenceEquals(_interruptGaugeAsset,presentation.Interruption?.Asset))
        {
            DestroyGaugeResources(gl);
            InterruptionGaugeFailure=null;
            // UI is authored in XY, with +Z towards the viewer. Engine cameras
            // use forward +X, left +Y, up +Z: do not reuse the world camera.
            var orientation = Quaternion.CreateFromRotationMatrix(new Matrix4x4(
                0,0,-1,0, -1,0,0,0, 0,1,0,0, 0,0,0,1));
            if (!EnbResolvedRenderCamera.TryCreateOrthographic(new Vector3(0,0,10), orientation,
                    new EnbRenderViewport(w,h,1),64,69,new EnbRenderClipRange(1,20),out var camera))
                throw new InvalidOperationException("Invalid scanner gauge camera.");
            var resources=new List<(EnbW3dRenderResource Resource,EnbW3dHierarchy Hierarchy,Matrix4x4 Parent)>
                { (presentation.Asset.Resource,presentation.Asset.Hierarchy,Matrix4x4.Identity) };
            if(presentation.Interruption is {} extra)resources.Add((extra.Asset.Resource,extra.Asset.Hierarchy,extra.Transform));
            foreach(var asset in resources)
            {
            int firstDraw=_gaugeDraws.Count;
            try
            {
            if(!EnbW3dHierarchyPose.TryEvaluate(asset.Resource.Hierarchies.First(h=>h.Name==asset.Hierarchy.Name),
                asset.Parent,out var transforms,out var poseError))throw new InvalidOperationException(poseError);
            var textures = new EnbRetailTextureResolver(assets.RetailMixResources!);
            foreach (var mesh in asset.Resource.Meshes
                         .Where(m => m.Name is not ("SCANNER_CLICK" or "INFO00_CLICK" or "INFO01_CLICK" or "FRAME_SELECT"))
                         .OrderByDescending(m => m.StaticSort.RuntimeLevel)
                         .ThenBy(m=>asset.Hierarchy.Pivots.First(p=>p.Name==m.Name).Ordinal))
            {
                var pivot = asset.Hierarchy.Pivots.First(p => p.Name == mesh.Name);
                var transform=transforms[pivot.Ordinal];
                foreach (var pass in mesh.MaterialPasses)
                {
                    var packet = new EnbW3dDrawTask(
                        new(EnbRenderPassKind.ModernOverlayBoundary,EnbW3dDrawBlendClass.Blended,
                            mesh.StaticSort,asset.Resource.Key,mesh.Ordinal,pass.Ordinal,1,0),
                        1,1,0,transform,new(transform.Translation,mesh.BoundingRadius),
                        mesh,null,new(pass,pass.SingleBindingSemantics),EnbW3dLightEnvironment.BlackNoLights,
                        time,asset.Hierarchy.Name,pivot.Ordinal,mesh.Name);
                    if (!EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out var error) ||
                        !TryCompileLane(submission,camera,EnbRenderTextureRuntimeSettings.RetailDefaults,
                            EnbRenderTextureBackendCapabilities.ModernDefault,textures,
                            new Dictionary<string,EnbTextureImage>(),null,LiveLaneKind.Textured,out var lane,out error) ||
                        !TryCreateGpuResource(gl,lane,LiveLaneKind.Textured,0,out var gpu,out error))
                        throw new InvalidOperationException("Scanner gauge: " + error);
                    int kind=mesh.Name.StartsWith("HULL_BAR",StringComparison.Ordinal)?0:
                        mesh.Name.StartsWith("SHIELD_BAR",StringComparison.Ordinal)?1:
                        mesh.Name.StartsWith("INTER_BAR",StringComparison.Ordinal)?2:
                        mesh.Name.StartsWith("INTER2_BAR",StringComparison.Ordinal)?3:-1;
                    _gaugeDraws.Add((new EnbW3dOverlayDrawOwner(lane.Textured!),gpu,kind,asset.Hierarchy.Name=="TSI08C"));
                }
            }
            }
            catch(Exception ex) when(asset.Hierarchy.Name=="TSI08C")
            {
                // Optional authored display failure must not stop world or
                // ordinary scanner rendering. Do not invent replacement art.
                foreach(var failed in _gaugeDraws.Skip(firstDraw))
                    failed.Gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);
                _gaugeDraws.RemoveRange(firstDraw,_gaugeDraws.Count-firstDraw);
                InterruptionGaugeFailure=ex.Message;
            }
            }
            // Stable insertion order within each static-sort level. Membership
            // changes only when these resource owners are rebuilt.
            for(int i=1;i<_gaugeDraws.Count;i++)
            {
                var draw=_gaugeDraws[i];
                int level=draw.Owner.Lane.Submission.Packet.Mesh.StaticSort.RuntimeLevel;
                int j=i;
                while(j>0 && _gaugeDraws[j-1].Owner.Lane.Submission.Packet.Mesh.StaticSort.RuntimeLevel<level)
                {_gaugeDraws[j]=_gaugeDraws[j-1];j--;}
                _gaugeDraws[j]=draw;
            }
            _gaugeAsset = presentation.Asset;
            _interruptGaugeAsset=presentation.Interruption?.Asset;
            _gaugeSize = (w,h);
        }
        int target = _gaugeFramebuffer.Begin(gl,fb,x,y,w,h);
        try
        {
            foreach (var draw in _gaugeDraws)
            {
                if(draw.Interruption!=interruptionOnly)continue;
                var source = draw.Owner.Lane;
                bool trailing=source.Submission.Packet.Mesh.Name.EndsWith('1');
                float ratio=draw.Kind switch
                {
                    0=>trailing?_hullTrail:presentation.Hull,
                    1=>trailing?_shieldTrail:presentation.Shield,
                    2=>trailing?_interruptTrail:presentation.Interruption!.Progress,
                    3=>trailing?_activationTrail:presentation.Interruption!.Activation,
                    _=>0,
                };
                draw.Owner.Update(time,source.Submission.Packet.Material.Source.Ordinal == 0 &&
                        draw.Kind>=0
                        ? new Vector2(0,EnbCurrentRetailGaugeRuntime.TextureOffset(ratio)) : null,null);
                var lane = LiveLanePlan.From(source);
                SubmitGpuResource(gl,target,w,h,draw.Gpu,lane,new(false,0,0,0),default,null,time);
            }
        }
        finally
        {
            _gaugeFramebuffer.End(gl,fb,x,y);
            gl.Viewport(0,0,width,height);
        }
    }
}
