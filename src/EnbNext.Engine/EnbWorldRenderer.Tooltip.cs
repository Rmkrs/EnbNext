using System.Numerics;
using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private EnbUiTooltipDraw? _tooltipPresentation;
    private EnbAuthoredTooltipFrame? _tooltipFrameAsset;
    private EnbUiSize _tooltipFrameSize;
    private (int W,int H) _tooltipGpuSize;
    private LiveLanePlan _tooltipLane;
    private LiveGpuResource? _tooltipGpu;
    private readonly EnbOpenGlScannerFramebuffer _tooltipFramebuffer = new();
    private readonly EnbOpenGlBackdropRenderer _tooltipTitleRenderer = new();
    private readonly EnbOpenGlBackdropRenderer _tooltipDetailRenderer = new();
    private readonly EnbOpenGlBackdropRenderer _tooltipThirdRenderer = new();
    private readonly List<EnbOpenGlBackdropRenderer> _tooltipLines = [];
    private bool _tooltipOnly, _tooltipFitsBounds;
    /// <summary>Next fixed tooltip position (opt-in); null keeps the tooltips at the pointer.</summary>
    internal EnbFixedTooltip? FixedTooltip { get; set; }
    /// <summary>Next Modern UI: every in-game tooltip in the Modern UI style (EnbModernTooltip) instead of the authored frame.</summary>
    internal bool ModernTooltips { get; set; }
    private readonly EnbModernTooltip _modernTooltip = new();
    private readonly EnbOpenGlBackdropRenderer _tooltipBackgroundRenderer = new();

    // One transparent HUD compositor surface. Verb art/highlights draw first,
    // followed by the complete tooltip, independently of the world snapshot tick.
    public static EnbWorldRenderer CreateTooltipLayer(Func<EnbRetailAssetBundle?> assets, Func<uint?> session)
        => new(() => null, assets, session, enableAudio: false, useMainViewEnvironment: false,
            clearColor: Vector4.Zero, continuousPresentation: false)
        { _tooltipOnly = true };

    public void SetTooltipPresentation(EnbUiTooltipDraw? presentation)
    {
        if (Equals(Volatile.Read(ref _tooltipPresentation), presentation)) return;
        Volatile.Write(ref _tooltipPresentation, presentation);
        RequestFrame();
    }

    private void DestroyTooltipResources(EnbGlApi? gl)
    {
        _tooltipGpu?.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);
        _tooltipGpu = null;
        _tooltipLane = default;
        _tooltipFrameAsset = null;
    }

    private void RenderTooltip(EnbGlApi gl,int fb,int width,int height,double scaling)
    {
        var viewport=new EnbUiSize(width/scaling,height/scaling);
        var area=FixedTooltip?.Area(viewport);
        if (_retailAssetsAccessor() is not { } assets) return;
        if (((MultiboxMenu is {OwnsTooltip:true} menu?menu.Tooltip(new(width/scaling,height/scaling),scaling):DamagePanel is {OwnsTooltip:true} meter?meter.Tooltip(new(width/scaling,height/scaling),scaling):LaunchPanel is {OwnsTooltip:true} panel?panel.Tooltip(new(width/scaling,height/scaling),scaling):MissionTracker is {OwnsTooltip:true} tracker?tracker.Tooltip(new(width/scaling,height/scaling),scaling):ModernHud is {OwnsTooltip:true} modern?modern.Tooltip(new(width/scaling,height/scaling),scaling):Volatile.Read(ref _tooltipPresentation)) ?? (area is null ? null : FixedTooltip!.Preview(assets,viewport,scaling))) is not { } p) return;
        if (ModernTooltips && p.Text is not null && _modernTooltip.Create(p, viewport, scaling) is { } restyled) p = restyled;
        // The fixed position moves the whole tooltip; its content is laid out relative to it.
        var bounds=area is {} fixedArea?EnbTooltipPlacement.Fixed(p.Bounds,fixedArea,viewport):p.Bounds;
        int x=(int)Math.Round(bounds.X*scaling), y=height-(int)Math.Round(bounds.Bottom*scaling);
        int w=(int)Math.Round(bounds.Width*scaling), h=(int)Math.Round(bounds.Height*scaling);
        //517C50 keeps a frame larger than the screen at its leading margin and
        //draws it clipped; only an empty rectangle has nothing to draw.
        if(w<1 || h<1) return;
        if (p.Background is null && (!ReferenceEquals(_tooltipFrameAsset,p.Asset) || _tooltipFrameSize!=p.DesignSize || _tooltipGpuSize!=(w,h) || _tooltipFitsBounds!=p.FitFrameToBounds))
        {
            DestroyTooltipResources(gl);
            var geometry=p.Asset.Geometry((float)p.DesignSize.Width,(float)p.DesignSize.Height,p.FitFrameToBounds);
            Vector3 min=geometry.Positions.Aggregate(Vector3.Min),max=geometry.Positions.Aggregate(Vector3.Max);
            var orientation=Quaternion.CreateFromRotationMatrix(new Matrix4x4(0,0,-1,0,-1,0,0,0,0,1,0,0,0,0,0,1));
            if(!EnbResolvedRenderCamera.TryCreateOrthographic((min+max)/2+new Vector3(0,0,10),orientation,
                new EnbRenderViewport(w,h,1),(p.FitFrameToBounds ? max.X-min.X : p.DesignSize.Width)/2,(p.FitFrameToBounds ? max.Y-min.Y : p.DesignSize.Height)/2,new(1,20),out var camera))
                throw new InvalidOperationException("Tooltip camera invalid.");
            var resource=p.Asset.Resource;
            var mesh=resource.Meshes.Single();
            var pass=mesh.MaterialPasses.Single();
            var packet=new EnbW3dDrawTask(new(EnbRenderPassKind.ModernOverlayBoundary,
                EnbW3dDrawBlendClass.Blended,mesh.StaticSort,resource.Key,mesh.Ordinal,pass.Ordinal,1,0),
                1,1,0,Matrix4x4.Identity,new((min+max)/2,Vector3.Distance(min,max)/2),mesh,geometry,
                new(pass,pass.SingleBindingSemantics),EnbW3dLightEnvironment.BlackNoLights,0,null,0,mesh.Name);
            if(!EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out var error) ||
                !TryCompileLane(submission,camera,EnbRenderTextureRuntimeSettings.RetailDefaults,
                    EnbRenderTextureBackendCapabilities.ModernDefault,assets.Textures,
                    new Dictionary<string,EnbTextureImage>(),null,LiveLaneKind.Textured,out _tooltipLane,out error) ||
                !TryCreateGpuResource(gl,_tooltipLane,LiveLaneKind.Textured,0,out _tooltipGpu,out error))
                throw new InvalidOperationException("Tooltip frame: "+error);
            _tooltipFrameAsset=p.Asset; _tooltipFrameSize=p.DesignSize; _tooltipGpuSize=(w,h); _tooltipFitsBounds=p.FitFrameToBounds;
        }
        int target=_tooltipFramebuffer.Begin(gl,fb,x,y,w,h);
        try
        {
            if (p.Background is { } background) DrawText(new(background, new(0, 0, bounds.Width, bounds.Height)), _tooltipBackgroundRenderer);
            else SubmitGpuResource(gl,target,w,h,_tooltipGpu!,_tooltipLane!,new(false,0,0,0),default,null,0);
            DrawText(p.Title, _tooltipTitleRenderer);
            DrawText(p.Detail, _tooltipDetailRenderer);
            DrawText(p.Third, _tooltipThirdRenderer);
            while (_tooltipLines.Count < p.Lines.Length) _tooltipLines.Add(new());
            for (int i = 0; i < p.Lines.Length; i++) DrawText(p.Lines[i], _tooltipLines[i]);
        }
        finally { _tooltipFramebuffer.End(gl,fb,x,y); gl.Viewport(0,0,width,height); }

        void DrawText(EnbUiTextDraw? text, EnbOpenGlBackdropRenderer renderer)
        {
            if (text is null) return;
            if (!renderer.IsInitialized) renderer.Initialize(gl);
            var r = text.Bounds;
            if(text.Clip is {} clip)
            {
                gl.Enable(0x0C11);
                HudScissor(gl)(
                    (int)Math.Ceiling(clip.Left*scaling),h-(int)Math.Floor(clip.Bottom*scaling),
                    Math.Max(0,(int)Math.Floor(clip.Right*scaling)-(int)Math.Ceiling(clip.Left*scaling)),
                    Math.Max(0,(int)Math.Floor(clip.Bottom*scaling)-(int)Math.Ceiling(clip.Top*scaling)));
            }
            try
            {
            renderer.RenderOverlay(gl, target, (int)Math.Round(r.X * scaling),
                h - (int)Math.Round(r.Bottom * scaling),
                Math.Max(1, (int)Math.Round(r.Width * scaling)),
                Math.Max(1, (int)Math.Round(r.Height * scaling)), text.Raster);
            }
            finally { if(text.Clip is not null)gl.Disable(0x0C11); }
        }
    }
}
