using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private ImmutableList<EnbUiModelDraw> _verbPresentation = [];
    private ImmutableList<EnbUiModelDraw> _skillMenuPresentation = [];
    private ImmutableList<EnbUiModelDraw> _optionsPresentation = [];
    internal EnbGroupLaunchPanel? LaunchPanel {get;set;} internal EnbDamageMeterPanel? DamagePanel {get;set;} internal EnbMultiboxMenu? MultiboxMenu {get;set;} internal EnbModernMenu? ModernMenu {get;set;} internal EnbWaypointArrow? WaypointArrow {get;set;} internal EnbMapNoteEditor? MapNoteEditor {get;set;} internal bool WaypointArrowOnTop {get;set;} internal EnbMissionTracker? MissionTracker {get;set;} internal EnbModernHud? ModernHud {get;set;} internal EnbWorldRenderer? ModernTargetSource {get;set;} internal bool ScannerChromeHidden {get;set;} internal void RequestPanelFrame()=>RequestFrame();
    private ImmutableList<EnbUiModelDraw> _inventoryPresentation = [];
    private ImmutableList<EnbUiModelDraw> _chatArt = [];
    public void SetChatArt(ImmutableList<EnbUiModelDraw> presentation)
    {
        ObserveVerbFlashes("chat", presentation);
        Volatile.Write(ref _chatArt, presentation);
        RequestFrame();
    }
    public void SetInventoryPresentation(ImmutableList<EnbUiModelDraw> presentation)
    {
        ObserveVerbFlashes("inventory", presentation);
        Volatile.Write(ref _inventoryPresentation, presentation);
        RequestFrame();
    }
    public void SetOptionsPresentation(ImmutableList<EnbUiModelDraw> presentation)
    {
        ObserveVerbFlashes("options", presentation);
        Volatile.Write(ref _optionsPresentation, presentation);
        RequestFrame();
    }
    private readonly List<EnbOpenGlBackdropRenderer> _skillMenuImages = [];

    public void SetSkillMenuPresentation(ImmutableList<EnbUiModelDraw> presentation)
    {
        ObserveVerbFlashes("skills", presentation);
        Volatile.Write(ref _skillMenuPresentation, presentation);
        RequestFrame();
    }
    private readonly EnbOpenGlScannerFramebuffer _verbFramebuffer = new();
    private bool _retainedVerbSubmission=Environment.GetEnvironmentVariable("ENB_LEGACY_UI_SUBMISSION")!="1";
    private readonly Dictionary<(EnbAuthoredUiAsset Asset, int W, int H, bool Disabled, bool Highlight, string? Reflection, EnbUiRect Design),
        List<(LiveLanePlan Lane, LiveGpuResource Gpu)>> _verbGpu = [];

    public void SetVerbPresentation(ImmutableList<EnbUiModelDraw> presentation)
    {
        if (Volatile.Read(ref _verbPresentation).SequenceEqual(presentation)) return;
        ObserveVerbFlashes("verbs", presentation);
        Volatile.Write(ref _verbPresentation, presentation);
        RequestFrame();
    }

    private void DestroyVerbResources(EnbGlApi? gl,bool preserveScene=false)
    {
        foreach (var draws in _verbGpu.Values)
            foreach (var draw in draws) draw.Gpu.Destroy(gl, _unlitBackend, _litBackend, _texturedBackend);
        _verbGpu.Clear();DestroyPortrait(gl);_portraitFramebuffer.Deinitialize(gl);
        foreach (var image in _skillMenuImages) image.Deinitialize(gl);
        _inventoryModelRenderer?.ReleaseGpuContext(gl); if(!preserveScene)_inventoryModelRenderer = null;
        _inventoryModelFramebuffer.Deinitialize(gl);
    }

    private double _overlayArtMs, _overlayPortraitMs, _overlayImagesMs;
    public string OverlayRenderDiagnostic=>$"UI GPU CPU: art {_overlayArtMs:0.00} ms; portraits {_overlayPortraitMs:0.00} ms (pose {_portraitPoseMs:0.00}, upload {_portraitUploadMs:0.00}, draw {_portraitDrawMs:0.00}); images {_overlayImagesMs:0.00} ms; scratch allocations {_verbFramebuffer.StorageAllocationCount}; portrait rebuilds {PortraitBuildCount}";
    private void RenderVerbs(EnbGlApi gl, int fb, int width, int height, double scaling)
    {
        using var submissionMeasurement=new EnbHudSubmissionMeasurement(_texturedBackend);
        // The Modern UI chat panel is a Next panel: under the map and the other game windows.
        var chatArt = Volatile.Read(ref _chatArt); int modernChatIndex = chatArt.FindIndex(static b => b.LayoutGroup == EnbUiGroup.ModernChat);
        var modernChat = modernChatIndex >= 0 ? chatArt[modernChatIndex] : null; if (modernChat is not null) chatArt = chatArt.RemoveAt(modernChatIndex);
        var buttons = chatArt.AddRange(Volatile.Read(ref _verbPresentation)).AddRange(Volatile.Read(ref _skillMenuPresentation))
            .AddRange(Volatile.Read(ref _inventoryPresentation)).AddRange(Volatile.Read(ref _optionsPresentation));
        // Next Modern UI buttons on the HUD (autopilot, damage meter) lie with the Next panels, under the map.
        var nextButtons = buttons.Exists(static b => b.NextPanel) ? buttons.FindAll(static b => b.NextPanel) : null;
        if (nextButtons is not null) buttons = buttons.RemoveAll(static b => b.NextPanel);
        // The Next-only multibox and damage meter panels sit underneath every
        // game window, including full-screen terminal backdrops.
        var panels=(LaunchPanel?.Draw(new(width/scaling,height/scaling),scaling)??[]).AddRange(DamagePanel?.Draw(new(width/scaling,height/scaling),scaling)??[])
            .AddRange(WaypointArrowOnTop?[]:WaypointArrow?.Draw(new(width/scaling,height/scaling),scaling)??[])
            .AddRange(MissionTracker?.Draw(new(width/scaling,height/scaling),scaling)??[])
            .AddRange(ModernHud?.Draw(new(width/scaling,height/scaling),scaling)??[]);
        if(modernChat is not null)panels=panels.Add(modernChat);
        if(nextButtons is not null)panels=panels.AddRange(nextButtons);
        // The maps: the sector map and (Next Modern UI) the galaxy map window, the one used last on top.
        // A Modern UI map window lies under its map, above the other Next panels and the map under it.
        EnbMapDraw? sectorMap=Volatile.Read(ref _mapPresentation),galaxyMap=Volatile.Read(ref _galaxyMapPresentation);
        bool galaxyFirst=galaxyMap is not null && (sectorMap is null || galaxyMap.Order<sectorMap.Order);
        EnbMapDraw? firstMap=galaxyFirst?galaxyMap:sectorMap,secondMap=galaxyFirst?sectorMap:galaxyMap;
        bool mapsShown=UiLayout?.Visible(EnbUiGroup.Map)!=false;
        if(mapsShown&&firstMap is {Chrome.IsEmpty:false})panels=panels.AddRange(firstMap.Chrome);
        int firstMapDraws=panels.Count;
        if(mapsShown&&secondMap is {Chrome.IsEmpty:false})panels=panels.AddRange(secondMap.Chrome);
        int panelDraws=panels.Count;
        buttons=panels.AddRange(buttons.OrderBy(b=>b.BehindHud?0:1))
            // The multibox quick menu opens from a utility button, so it lies on top like a context menu.
            .AddRange(MultiboxMenu?.Draw(new(width/scaling,height/scaling),scaling)??[])
            // Next Modern UI pop-up menus (help, player) lie on top of every window.
            .AddRange(ModernMenu?.Draw(new(width/scaling,height/scaling),scaling)??[])
            // While Navigation Options previews arrow settings, the arrow shows above the options window.
            .AddRange(WaypointArrowOnTop?WaypointArrow?.Draw(new(width/scaling,height/scaling),scaling)??[]:[])
            // The map marker editor is modal and lies above everything else.
            .AddRange(MapNoteEditor?.Draw(new(width/scaling,height/scaling),scaling)??[]);
        int imageIndex = 0;
        // The map is a game window: above the Next panels (Modern UI frames included), below the other game windows.
        int mapsDrawn=0;
        void Map(int before=int.MaxValue)
        {
            if(mapsDrawn==0&&before>=firstMapDraws){mapsDrawn=1;if(firstMap is not null)using (FullClientBenchmarkMeter.Enter("hud-map-draw"))RenderMap(gl,fb,width,height,scaling,firstMap,galaxyFirst);}
            if(mapsDrawn==1&&before>=panelDraws){mapsDrawn=2;if(secondMap is not null)using (FullClientBenchmarkMeter.Enter("hud-map-draw"))RenderMap(gl,fb,width,height,scaling,secondMap,!galaxyFirst);}
        }
        if (buttons.Count == 0) { DestroyVerbResources(gl); Map(); RenderChat(gl,fb,width,height,scaling); return; }
        if (_retailAssetsAccessor() is not { } assets) { Map(); RenderChat(gl,fb,width,height,scaling); return; }
        // Resizes and asset reloads must not accumulate obsolete GPU allocations.
        foreach (var key in _verbGpu.Keys.Where(k => !buttons.Any(b => ReferenceEquals(b.Asset,k.Asset) &&
                     (int)Math.Round(LayoutRect(b.LayoutGroup,b.Bounds).Width*scaling)==k.W && (int)Math.Round(LayoutRect(b.LayoutGroup,b.Bounds).Height*scaling)==k.H && b.DesignBounds==k.Design)).ToArray())
        {
            foreach (var draw in _verbGpu[key]) draw.Gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);
            _verbGpu.Remove(key);
        }
        // Preserve the existing single-view cache across character/customization
        // panel toggles. Trade actors only live while their views are present.
        foreach(var key in _portraits.Keys.Where(k=>k!="default" && !buttons.Any(b=>b.Portrait is not null && b.PortraitCacheKey==k)).ToArray())
        {
            DestroyPortrait(gl,_portraits[key]);_portraits.Remove(key);
        }
        _portraitPoseMs=_portraitUploadMs=_portraitDrawMs=0;
        long artTicks=0,portraitTicks=0,imageTicks=0;
        bool chatDrawn=false;int drawIndex=0;
        foreach (var button in buttons)
        {
            // Chat text follows the game's behind-HUD art (its own backdrop);
            // the panels before that art must not move it underneath.
            Map(drawIndex);
            if(drawIndex++>=panelDraws && !button.BehindHud && !chatDrawn)
            { RenderChat(gl,fb,width,height,scaling);chatDrawn=true; }
            var group=button.LayoutGroup;if(UiLayout?.Visible(group)==false)continue;var bounds=LayoutRect(group,button.Bounds);
            int x=(int)Math.Round(bounds.X*scaling), y=height-(int)Math.Round(bounds.Bottom*scaling);
            int w=(int)Math.Round(bounds.Width*scaling), h=(int)Math.Round(bounds.Height*scaling);
            // Authored terminal buttons extend slightly beyond the viewport.
            // The framebuffer blit clips their edges; keep the visible portion.
            if(w<1 || h<1 || x+w<=0 || y+h<=0 || x>=width || y>=height) continue;
            bool disabled=!EnbCurrentRetailVerbPresentation.IsEnabled(button.Verb);
            long stageStarted=System.Diagnostics.Stopwatch.GetTimestamp();
            // Next overlays such as the waypoint arrow carry only images.
            int target=fb;
            if(!button.ImagesOnly)
            {
            using (FullClientBenchmarkMeter.Enter("hud-art-copy-in"))
                target=_verbFramebuffer.Begin(gl,fb,x,y,w,h);
            try
            {
                using var artScope=FullClientBenchmarkMeter.Enter("hud-art-draw");
                if(button.ClearBackground || button.OpaqueBlackBacking || button.OpaqueBackingColor is not null)
                {
                    Vector3 backing = button.OpaqueBackingColor ?? Vector3.Zero;
                    gl.ClearColor(backing.X, backing.Y, backing.Z, 1);
                    gl.Clear(EnbGlConstants.GL_COLOR_BUFFER_BIT);
                }
                Draw(false);
                if(button.Hovered && !disabled) Draw(true);
                if(button.OpaqueBlackBacking)
                {
                    // Authored blends may write transparent alpha. Keep this panel
                    // opaque when the host composites it over the world surface.
                    var mask=HudColorMask(gl);
                    mask(0,0,0,1);
                    gl.ClearColor(0,0,0,1);gl.Clear(EnbGlConstants.GL_COLOR_BUFFER_BIT);
                    mask(1,1,1,1);
                }
            }
            finally
            {
                using var copyScope=FullClientBenchmarkMeter.Enter("hud-art-copy-out");
                _verbFramebuffer.End(gl,fb,x,y); gl.Viewport(0,0,width,height);
            }
            }
            artTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-stageStarted;stageStarted=System.Diagnostics.Stopwatch.GetTimestamp();
            if (button.EquipmentPreview is { } preview) RenderEquipmentPreview(gl, fb, width, height, scaling, LayoutRect(group,button.EquipmentBounds??button.Bounds), preview, button.EquipmentView,button.EquipmentTransparent,button.EquipmentPan,button.EquipmentSpin,button.EquipmentRotation,LayoutRect(group,button.EquipmentClipBounds));
            if(button.Portrait is {} avatar)RenderPortrait(gl,fb,width,height,scaling,LayoutRect(group,button.PortraitBounds),avatar,button.PortraitEnvironment,button.PortraitFullBody,button.PortraitHeading,button.CustomizationView,button.PortraitAspect,button.PortraitCacheKey,
                button.PortraitStationLocal ? StationLocalPortraitAccessor?.Invoke(avatar) : button.PortraitStationRemoteId is uint remoteId ? StationRemotePortraitAccessor?.Invoke(remoteId,avatar) : button.PortraitPose,button.PortraitSpeech,button.PortraitSharedActor);
            if(button.ScannerModel)
            {
                ModernTargetSource?.RenderModernTarget(gl,fb,width,height,scaling);
                // The model's blends write alpha; keep the portrait square opaque so the world does not show through.
                var square=LayoutRect(group,button.PortraitBounds);
                int sx=(int)Math.Floor(square.X*scaling),sy=height-(int)Math.Ceiling(square.Bottom*scaling);
                gl.BindFramebuffer(EnbGlConstants.GL_FRAMEBUFFER,fb);gl.Enable(0x0C11);
                HudScissor(gl)(sx,sy,(int)Math.Ceiling(square.Right*scaling)-sx,height-(int)Math.Floor(square.Y*scaling)-sy);
                var mask=HudColorMask(gl);mask(0,0,0,1);gl.ClearColor(0,0,0,1);gl.Clear(EnbGlConstants.GL_COLOR_BUFFER_BIT);mask(1,1,1,1);
                gl.Disable(0x0C11);
            }
            if(button.Portrait is not null)button.PreviewPanOrigin?.Invoke(_portraitPanOrigin);
            if(button.PreviewDistance is {} report)report(button.Portrait is not null?_portraitCameraDistance:_inventoryCameraDistance);
            portraitTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-stageStarted;stageStarted=System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var image in button.Images)
            {
                if (_skillMenuImages.Count <= imageIndex) _skillMenuImages.Add(new());
                var renderer = _skillMenuImages[imageIndex++];
                if (!renderer.IsInitialized) renderer.Initialize(gl);
                var r = LayoutRect(group,image.Bounds);
                if(LayoutRect(group,image.Clip) is {} clip)
                {
                    gl.Enable(0x0C11);
                    HudScissor(gl)(
                        (int)Math.Ceiling(clip.X*scaling),height-(int)Math.Floor(clip.Bottom*scaling),
                        Math.Max(0,(int)Math.Floor(clip.Right*scaling)-(int)Math.Ceiling(clip.X*scaling)),
                        Math.Max(0,(int)Math.Floor(clip.Bottom*scaling)-(int)Math.Ceiling(clip.Y*scaling)));
                }
                try
                {
                using var imageScope=FullClientBenchmarkMeter.Enter("hud-image-draw");
                renderer.RenderOverlay(gl,fb,(int)Math.Round(r.X*scaling),height-(int)Math.Round(r.Bottom*scaling),
                    Math.Max(1,(int)Math.Round(r.Width*scaling)),Math.Max(1,(int)Math.Round(r.Height*scaling)),image.Raster,
                    verticalOffset:image.Clip is null?0:Math.Round(r.Bottom*scaling)-r.Bottom*scaling,
                    opacity:image.Opacity,verticalScale:image.VerticalScale,brightness:image.Brightness);
                }
                finally { if(image.Clip is not null)gl.Disable(0x0C11); }
            }

            imageTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-stageStarted;
            _overlayArtMs=artTicks*1000d/System.Diagnostics.Stopwatch.Frequency;_overlayPortraitMs=portraitTicks*1000d/System.Diagnostics.Stopwatch.Frequency;_overlayImagesMs=imageTicks*1000d/System.Diagnostics.Stopwatch.Frequency;
            void Draw(bool highlight)
            {
                EnbCurrentRetailEnvironmentAtlasSet? swatchAtlas=null;
                if(button.SpinSwatch)
                {
                    // The retail editor uses SClient's shared environment manager
                    // (0054BD30); PROCTEX bindings refer to its rebuilt surfaces.
                    if(assets.EnvironmentMaps is not {} maps || !maps.TryResolveReflection(EnbCurrentRetailStationInterior.ReflectionBaseAsset,out var reflection))
                        throw new InvalidOperationException("Swatch reflection selection unavailable.");
                    swatchAtlas=ResolveEnvironmentAtlasSet(assets,maps,reflection,0,out var atlasError);
                    if(swatchAtlas is null)throw new InvalidOperationException(atlasError);
                }
                var key=(button.Asset,w,h,disabled,highlight,swatchAtlas?.ReflectionTexture,button.DesignBounds);
                if(!_verbGpu.TryGetValue(key,out var draws))
                {
                    draws=[];
                    _verbGpu.Add(key,draws);
                    var orientation=Quaternion.CreateFromRotationMatrix(new Matrix4x4(0,0,-1,0,-1,0,0,0,0,1,0,0,0,0,0,1));
                    var design = button.DesignBounds;
                    // Some authored UI controls are genuinely 3-D (BUY_* metal
                    // swatches extend beyond +/-16 in Z). The old fixed 1..20
                    // clip interval cut their front faces off.
                    var depths=button.Asset.ModelDepths();
                    // Text-only menu entries can have an empty art resource.
                    float minimumDepth=depths.DefaultIfEmpty(0).Min(),maximumDepth=depths.DefaultIfEmpty(0).Max();
                    // Authored separators can occupy one physical pixel at 800x600.
                    // Orthographic extents come from design bounds; the camera's minimum
                    // viewport does not change the actual w/h framebuffer or raster size.
                    if(!EnbResolvedRenderCamera.TryCreateOrthographic(new Vector3((float)design.Center.X,-(float)design.Center.Y,maximumDepth+10),orientation,
                            new EnbRenderViewport(Math.Max(2,w),Math.Max(2,h),1),design.Width/2,design.Height/2,new(1,maximumDepth-minimumDepth+20),out var camera))
                        throw new InvalidOperationException("Invalid verb camera.");
                    if(button.SpinSwatch && !EnbResolvedRenderCamera.TryCreate(new(0,0,320),orientation,new(640,480,1),Math.PI/2,4.0/3,new(1,3000),out camera))
                        throw new InvalidOperationException("Invalid retail swatch camera.");
                    var resource=button.Asset.Resource;
                    // Retail drains each static-sort level FIFO in hierarchy order, not mesh-file order.
                    // Trade's backdrop and handshake share level 15; the file lists the handshake first.
                    foreach(var mesh in resource.Meshes.OrderByDescending(m=>m.StaticSort.RuntimeLevel)
                                .ThenBy(m=>button.Asset.Geometry is not null ? m.Ordinal : (button.Asset.Hierarchy.Pivots.Count==0?m.Ordinal:button.Asset.Hierarchy.Pivots.First(p=>p.Name==m.Name).Ordinal)))
                    {
                        // Resizable windows have already been skinned into window coordinates.
                        // Their FRAME mesh is attached through bones, not a same-named static pivot.
                        int pivotOrdinal=button.Asset.Geometry is not null ? 0 : (button.Asset.Hierarchy.Pivots.Count==0?0:button.Asset.Hierarchy.Pivots.First(p=>p.Name==mesh.Name).Ordinal);
                        var transform=button.Asset.Geometry is null?button.Asset.ModelTransform(mesh.Name):Matrix4x4.Identity;
                        var translation=transform.Translation;
                        foreach(var authored in mesh.MaterialPasses)
                        {
                            var pass=EnbCurrentRetailVerbPresentation.Apply(authored,highlight,disabled);
                            var packet=new EnbW3dDrawTask(new(EnbRenderPassKind.ModernOverlayBoundary,EnbW3dDrawBlendClass.Blended,
                                mesh.StaticSort,resource.Key,mesh.Ordinal,pass.Ordinal,1,0),1,1,0,
                                transform,new(translation,mesh.BoundingRadius),mesh,button.Asset.Geometry,
                                new(pass,pass.SingleBindingSemantics),EnbW3dLightEnvironment.BlackNoLights,0,null,pivotOrdinal,mesh.Name);
                            if(!EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out var error) ||
                                !TryCompileLane(submission,camera,EnbRenderTextureRuntimeSettings.RetailDefaults,
                                    EnbRenderTextureBackendCapabilities.ModernDefault,assets.Textures,new Dictionary<string,EnbTextureImage>(),
                                    swatchAtlas,LiveLaneKind.Textured,out var lane,out error) ||
                                !TryCreateGpuResource(gl,lane,LiveLaneKind.Textured,0,out var gpu,out error))
                                throw new InvalidOperationException("Verb GPU " + mesh.Name + ": "+error);
                            draws.Add((lane,gpu));
                        }
                    }
                }
                // Only this mesh loop owns GL state. Framebuffer copies, portraits,
                // glyphs and external overlays remain outside its cache lifetime.
                using var submissionScope=_retainedVerbSubmission ? _texturedBackend.BeginRetainedSubmission(gl,true) : null;
                foreach(var draw in draws)
                {
                    var lane = draw.Lane;
                    if(lane.Textured is {} ui && (button.SpinSwatch || highlight&&button.FlashHighlight))
                    {
                        var lighting=ui.LightingPlan;
                        if(button.SpinSwatch)
                        {
                            var positions=button.Asset.Geometry!.Positions;
                            var center=new Vector3((float)button.DesignBounds.Center.X,-(float)button.DesignBounds.Center.Y,(positions.Min(p=>p.Z)+positions.Max(p=>p.Z))*.5f);
                            var rotation=Matrix4x4.CreateTranslation(-center)*Matrix4x4.CreateRotationY(button.SwatchAngle)*Matrix4x4.CreateTranslation(center);
                            var d=button.DesignBounds;
                            var crop=Matrix4x4.CreateScale((float)(640/d.Width),(float)(480/d.Height),1)*Matrix4x4.CreateTranslation((float)(-2*d.Center.X/d.Width),(float)(2*d.Center.Y/d.Height),0);
                            lighting=(lighting).Copy().SetCanonicalClipFromModel(rotation*lighting.CanonicalClipFromModel*crop).SetCanonicalViewFromModel(rotation*lighting.CanonicalViewFromModel).SetModelToWorld(rotation*lighting.ModelToWorld).SetNormalFromModel(Matrix4x4.CreateRotationY(button.SwatchAngle)*lighting.NormalFromModel);
                        }
                        if(highlight&&button.FlashHighlight)
                        {
                            float pulse=EnbWidgetFlash.Emissive(button.HighlightTime ?? unchecked((uint)Environment.TickCount64)); // 006609A0
                            lighting=(lighting).Copy().SetRunStates(lighting.RunStates.Select(r=>(r).Copy().SetMaterial(r.Material with {Emissive=new(pulse)})).ToImmutableArray());
                        }
                        lane=LiveLanePlan.From((ui).Copy().SetLightingPlan(lighting).SetRunStates(ui.RunStates.Select((r,i)=>r with {LightingRun=lighting.RunStates[i]}).ToImmutableArray()));
                    }
                    if (button.Stage0UvOffset is { } offset && lane.Textured is { } source && source.Submission.Packet.Material.Source.Ordinal == 0)
                    {
                        var packet = (source.Submission.Packet).ForkTask().SetCurrentRetailStage0UvOffset(offset);
                        lane = LiveLanePlan.From((source).Copy().SetLightingPlan((source.LightingPlan).Copy().SetSubmission(source.Submission with { Packet = packet })));
                    }
                    if((highlight ? button.HighlightEmissive is {} highlightEmissive ? new Vector3(highlightEmissive) : (Vector3?)null : button.EmissiveOverride) is {} emissive && lane.Textured is {} tinted)
                    {
                        var lighting=(tinted.LightingPlan).Copy().SetRunStates(tinted.LightingPlan.RunStates.Select(r=>(r).Copy().SetMaterial(r.Material with {Emissive=emissive})).ToImmutableArray());
                        lane=LiveLanePlan.From((tinted).Copy().SetLightingPlan(lighting).SetRunStates(tinted.RunStates.Select((r,i)=>r with {LightingRun=lighting.RunStates[i]}).ToImmutableArray()));
                    }
                    if(button.ShortcutRangeFrame is int rangeFrame && lane.Textured is {} rangeLane)
                        lane=LiveLanePlan.From(EnbCurrentRetailVerbPresentation.ApplyShortcutRange(rangeLane,rangeFrame,button.ShortcutRangeAngle));
                    uint time=button.MaterialTime ?? (button.AnimateMaterials?unchecked((uint)Environment.TickCount64):0);
                    if(button.AnimateMaterials && lane.Textured is {} animated)
                        lane=LiveLanePlan.From((animated).Copy().SetLightingPlan((animated.LightingPlan).Copy().SetSubmission(animated.Submission with
                            {Packet=(animated.Submission.Packet).ForkTask().SetFrameTimeMilliseconds(time)})));
                    SubmitGpuResource(gl,target,w,h,draw.Gpu,lane,new(false,0,0,0),default,null,time,compositeOpaqueCoverage:button.CompositeOpaqueCoverage);
                }
            }
        }
        Map();
        if(!chatDrawn)RenderChat(gl,fb,width,height,scaling);
        if(buttons.Any(b=>b.ClearBackground))
        {
            // This HUD is a separate host compositor surface. Retail blends
            // into an opaque backbuffer; transparent material alpha must not
            // expose the independently rendered station underneath it.
            gl.BindFramebuffer(EnbGlConstants.GL_FRAMEBUFFER,fb);gl.Disable(0x0C11);
            var mask=HudColorMask(gl);
            mask(0,0,0,1);
            try {gl.ClearColor(0,0,0,1);gl.Clear(EnbGlConstants.GL_COLOR_BUFFER_BIT);}
            finally {mask(1,1,1,1);}
        }
    }
}
