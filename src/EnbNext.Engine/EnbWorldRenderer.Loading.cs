using System.Numerics;
using System.Runtime.InteropServices;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void LoadingColorMask(byte red,byte green,byte blue,byte alpha);
    private EnbLoadingDraw? _loadingPresentation;
    private EnbAuthoredUiAsset? _loadingAsset;
    private (int W,int H) _loadingSize;
    private bool _loadingLocations;
    private System.Collections.Immutable.ImmutableArray<EnbLoadingMarker> _loadingMarkers = [];
    private readonly List<(LiveLanePlan Lane,LiveGpuResource Gpu)> _loadingGpu = [];
    private readonly EnbOpenGlBackdropRenderer[] _loadingText = [new(),new(),new()];

    /// <summary>A loading screen is being presented (it stays visible while the interface is hidden).</summary>
    internal bool LoadingPresented => Volatile.Read(ref _loadingPresentation) is not null;
    public void SetLoadingPresentation(EnbLoadingDraw? presentation)
    {
        if (presentation is null && Volatile.Read(ref _loadingPresentation) is null) return;
        Volatile.Write(ref _loadingPresentation,presentation);
        RequestFrame();
    }

    private void DestroyLoadingResources(EnbGlApi? gl)
    {
        foreach(var draw in _loadingGpu) draw.Gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);
        _loadingGpu.Clear(); _loadingAsset=null;
        foreach(var text in _loadingText) text.Deinitialize(gl);
    }

    private void RenderLoading(EnbGlApi gl,int fb,int width,int height,double scaling,EnbLoadingDraw p)
    {
        gl.ClearColor(0,0,0,1);
        gl.Clear(EnbGlConstants.GL_COLOR_BUFFER_BIT | GlDepthBufferBit);
        if (!ReferenceEquals(_loadingAsset,p.Asset) || _loadingSize!=(width,height) || _loadingLocations!=p.ShowLocations ||
            !_loadingMarkers.SequenceEqual(p.Markers))
        {
            DestroyLoadingResources(gl);
            var orientation=Quaternion.CreateFromRotationMatrix(new Matrix4x4(0,0,-1,0,-1,0,0,0,0,1,0,0,0,0,0,1));
            if(!EnbResolvedRenderCamera.TryCreateOrthographic(new Vector3(0,0,10),orientation,
                new EnbRenderViewport(width,height,1),320,240,new(1,20),out var camera))
                throw new InvalidOperationException("Invalid loading camera.");
            Compile(p.Asset,Vector3.Zero,true);
            foreach(var marker in p.Markers) Compile(marker.Asset,marker.Position,false);
            _loadingAsset=p.Asset; _loadingSize=(width,height); _loadingLocations=p.ShowLocations; _loadingMarkers=p.Markers;

            void Compile(EnbAuthoredUiAsset asset,Vector3 offset,bool main)
            {
            var resource=asset.Resource;
            foreach(var mesh in resource.Meshes.OrderByDescending(m=>m.StaticSort.RuntimeLevel)
                .ThenBy(m=>asset.Hierarchy.Pivots.First(v=>v.Name==m.Name).Ordinal))
            {
                if (main && !p.ShowLocations && mesh.Name is "TEXT00" or "TEXT01") continue;
                var pivot=asset.Hierarchy.Pivots.First(v=>v.Name==mesh.Name);
                EnbAuthoredTargetPanelLoader.TryResolveTranslation(asset.Hierarchy,pivot.Ordinal,out var translation);
                translation+=offset;
                foreach(var pass in mesh.MaterialPasses)
                {
                    var packet=new EnbW3dDrawTask(new(EnbRenderPassKind.ModernOverlayBoundary,EnbW3dDrawBlendClass.Blended,
                        mesh.StaticSort,resource.Key,mesh.Ordinal,pass.Ordinal,1,0),1,1,0,Matrix4x4.CreateTranslation(translation),
                        new(translation,mesh.BoundingRadius),mesh,null,new(pass,pass.SingleBindingSemantics),
                        EnbW3dLightEnvironment.BlackNoLights,0,null,pivot.Ordinal,mesh.Name);
                    if(!EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out var error) ||
                        !TryCompileLane(submission,camera,EnbRenderTextureRuntimeSettings.RetailDefaults,
                            EnbRenderTextureBackendCapabilities.ModernDefault,_retailAssetsAccessor()!.Textures,
                            new Dictionary<string,EnbTextureImage>(),null,LiveLaneKind.Textured,out var lane,out error) ||
                        !TryCreateGpuResource(gl,lane,LiveLaneKind.Textured,0,out var gpu,out error))
                        throw new InvalidOperationException("Loading GPU: "+error);
                    _loadingGpu.Add((lane,gpu));
                }
            }
            }
        }
        foreach(var draw in _loadingGpu) SubmitGpuResource(gl,fb,width,height,draw.Gpu,draw.Lane,new(false,0,0,0),default,null,0);
        // During layout handoff, stretch map AND attached text as one composition.
        double sx=width/p.LogicalSize.Width, sy=height/p.LogicalSize.Height;
        for(int i=0;i<p.Text.Length;i++)
        {
            var text=p.Text[i]; var r=text.Bounds; var renderer=_loadingText[i];
            if(!renderer.IsInitialized) renderer.Initialize(gl);
            renderer.RenderOverlay(gl,fb,(int)Math.Round(r.X*sx),height-(int)Math.Round(r.Bottom*sy),
                Math.Max(1,(int)Math.Round(r.Width*sx)),Math.Max(1,(int)Math.Round(r.Height*sy)),text.Raster);
        }
        // Retail writes to an opaque backbuffer. Its authored alpha blending must
        // not make this modern compositor surface translucent over the retiring HUD.
        var mask=HudColorMask(gl);
        mask(0,0,0,1);
        gl.ClearColor(0,0,0,1); gl.Clear(EnbGlConstants.GL_COLOR_BUFFER_BIT);
        mask(1,1,1,1);
        CompleteCaptureLightingFrame();
    }
}
