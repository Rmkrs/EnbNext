using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private EnbMapDraw? _mapPresentation;
    /// <summary>Next Modern UI: the galaxy map's own window, drawn beside the sector map in the order they were used.</summary>
    private EnbMapDraw? _galaxyMapPresentation;
    private readonly List<EnbOpenGlBackdropRenderer> _galaxyMapImages = [];
    private readonly List<EnbOpenGlBackdropRenderer> _galaxyMapQuads = [];
    public void SetGalaxyMapPresentation(EnbMapDraw? value) { Volatile.Write(ref _galaxyMapPresentation, value); RequestFrame(); }
    private EnbMapDraw? _mapPreparation;
    private EnbAuthoredMapFrame? _failedMapPreparation;
    private (EnbAuthoredMapFrame? Asset,int Width,int Height) _preparedMapKey;
    internal string? MapPreparationError { get; private set; }
    internal int MapModelProgramInitializations { get; private set; }
    public void SetMapPreparation(EnbMapDraw? value)=>Volatile.Write(ref _mapPreparation,value);
    private void PreparePendingMap(EnbGlApi gl,int framebuffer,int width,int height,double scaling)
    {
        if(Volatile.Read(ref _mapPresentation) is not null)return;
        if(Volatile.Read(ref _mapPreparation) is {} p &&
            !ReferenceEquals(_failedMapPreparation,p.Frame) && (_preparedMapKey!=(p.Frame,width,height) || _mapPlanetOwner!=p.ModelOwner) &&
            (int)Math.Round(p.Size.Width*scaling)==width && (int)Math.Round(p.Size.Height*scaling)==height &&
            _retailAssetsAccessor() is {} assets)
        {
            var scratch=new EnbOpenGlScannerFramebuffer();
            try
            {
                // Frame-only GPU warmup: never publish pixels, sample world
                // models, or advance a mapper/label clock before map opening.
                int target=scratch.BeginScene(gl,framebuffer,width,height);
                RenderMapCore(gl,target,width,height,scaling,p,_mapImages,_mapQuads);
                PrepareMapModelPrograms(gl,p.ModelOwner);
                _preparedMapKey=(p.Frame,width,height);
                MapPreparationError=null;
            }
            catch(Exception ex) when(ex is InvalidDataException or InvalidOperationException or NotSupportedException)
            { _failedMapPreparation=p.Frame; MapPreparationError=ex.Message; }
            finally
            {
                scratch.Deinitialize(gl);
                gl.BindFramebuffer(EnbGlConstants.GL_FRAMEBUFFER,framebuffer); gl.Viewport(0,0,width,height);
            }
        }
    }
    private void PrepareMapModelPrograms(EnbGlApi gl,(uint? Session,ulong? Ship,uint? Sector) owner)
    {
        _inventoryModelRenderer ??= CreatePreviewModelRenderer(gl);
        // Compile programs while hidden, without constructing/sampling a world
        // model. CaptureMapPlanet still owns the first-open mapper timestamp.
        if(_mapPlanetOwner!=owner) { DestroyMapPlanets(gl); _mapPlanetOwner=owner; }
        if(_mapModelRenderer is null)
        {
            _mapModelRenderer=CreatePreviewModelRenderer(gl);MapModelProgramInitializations++;
        }
        if(_mapPlanetRenderer is null)
        {
            _mapPlanetRenderer=CreatePreviewModelRenderer(gl);MapModelProgramInitializations++;
            _mapPlanetRenderer.FrustumCullingEnabled=false;
        }
        if(_mapPlanetCompositor is null) { _mapPlanetCompositor=new(); _mapPlanetCompositor.InitializeMapTexture(gl); }
    }
    private EnbWorldRenderer CreatePreviewModelRenderer(EnbGlApi gl)
    {
        var renderer=new EnbWorldRenderer(()=>_retailAssetsAccessor()?.BaseAssetResolver,_retailAssetsAccessor,_fixedSessionIdAccessor,
            enableAudio:false,useMainViewEnvironment:false,continuousPresentation:false);
        renderer._mapModelDetail=true;
        renderer._graphicsOptionsOwner=this;
        try { renderer.InitializeGl(gl); }
        catch { renderer.ReleaseGpuContext(gl); throw; }
        return renderer;
    }
    private EnbWorldRenderer? _mapHelpRenderer;
    private EnbOpenGlBackdropRenderer? _mapRouteRenderer;
    private EnbWorldRenderer? _mapModelRenderer;
    private readonly EnbOpenGlScannerFramebuffer _mapModelFramebuffer = new();
    internal EnbWorldRenderer? MapWorldRenderer { get; set; }
    private bool _mapModelDetail;
    private readonly List<(LiveLanePlan Lane, LiveGpuResource Gpu, string Name, bool Highlight)> _mapDraws = [];
    private readonly List<EnbOpenGlBackdropRenderer> _mapImages = [];
    private readonly List<EnbOpenGlBackdropRenderer> _mapQuads = [];
    private (EnbAuthoredMapFrame? Asset, float Frame, int Width, int Height,Matrix4x4 Layout,int Packets) _mapKey;
    private readonly Dictionary<(EnbMapGlyph Glyph, EnbRgb Color, bool Preserve), List<(LiveLanePlan Lane, LiveGpuResource Gpu)>> _mapGlyphDraws = [];

    public void SetMapPresentation(EnbMapDraw? value)
    {
        ObserveGadgetFlashes("map", value is { Hovered: {} name } &&
            (name.EndsWith("_BTTN", StringComparison.Ordinal) || name == "EXIT") &&
            value.Packets.Any(p => p.RenderObjectName == name) ? [(object)(value.Frame, name)] : []);
        Volatile.Write(ref _mapPresentation, value);
        RequestFrame();
    }

    private void DestroyMapResources(EnbGlApi? gl,bool preserveScene=false)
    {
        _preparedMapKey=default;
        _mapRouteRenderer?.Deinitialize(gl); _mapRouteRenderer = null;
        _mapModelRenderer?.ReleaseGpuContext(gl); if(!preserveScene)_mapModelRenderer = null;
        _mapModelFramebuffer.Deinitialize(gl);
        DestroyMapPlanets(gl,preserveScene);
        _mapHelpRenderer?.ReleaseGpuContext(gl); if(!preserveScene)_mapHelpRenderer=null;
        foreach (var d in _mapDraws) d.Gpu.Destroy(gl, _unlitBackend, _litBackend, _texturedBackend);
        _mapDraws.Clear(); _mapKey = preserveScene?(_mapKey.Asset,_mapKey.Frame,0,0,Matrix4x4.Identity,0):default;
        foreach (var image in _mapImages) image.Deinitialize(gl);
        _mapImages.Clear();
        foreach (var quad in _mapQuads) quad.Deinitialize(gl);
        _mapQuads.Clear();
        foreach (var image in _galaxyMapImages) image.Deinitialize(gl);
        _galaxyMapImages.Clear();
        foreach (var quad in _galaxyMapQuads) quad.Deinitialize(gl);
        _galaxyMapQuads.Clear();
        foreach (var draws in _mapGlyphDraws.Values)
            foreach (var d in draws) d.Gpu.Destroy(gl, _unlitBackend, _litBackend, _texturedBackend);
        _mapGlyphDraws.Clear();
    }

    private EnbResolvedRenderCamera PrepareMapFrame(EnbGlApi gl,int width,int height,EnbMapDraw p,EnbRetailAssetBundle assets)
    {
        var key = (p.Frame, p.AnimationFrame, width, height,LayoutClip(EnbUiGroup.Map),p.Packets.Length);
        var orientation = Quaternion.CreateFromRotationMatrix(new Matrix4x4(0,0,-1,0,-1,0,0,0,0,1,0,0,0,0,0,1));
        if (!EnbResolvedRenderCamera.TryCreateOrthographic(new(0,0,10), orientation, new(width,height,1),320,240,new(1,20),out var camera))
            throw new InvalidOperationException("Map orthographic camera unavailable.");
        // The Next Modern UI window has no retail frame to compile (and two map windows must not evict each other's).
        if (p.Packets.IsEmpty) return camera;
        if (_mapKey != key)
        {
            if (!ReferenceEquals(_mapKey.Asset,p.Frame)) DestroyMapResources(gl);
            else { foreach (var d in _mapDraws) d.Gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend); _mapDraws.Clear(); }
            var decoded = new Dictionary<string, EnbTextureImage>();
            foreach (var original in p.Packets)
            foreach (bool highlight in original.RenderObjectName.EndsWith("_BTTN", StringComparison.Ordinal) || original.RenderObjectName == "EXIT" ? new[] { false, true } : new[] { false })
            {
                var pass = EnbCurrentRetailVerbPresentation.Apply(original.Material.Source, highlight, false);
                var packet = (original).ForkTask().SetMaterial(new(pass, pass.SingleBindingSemantics));
                if (!EnbW3dIndexedSubmissionCompiler.TryCompile(packet, out var submission, out var error) ||
                    !TryCompileLane(submission, camera, EnbRenderTextureRuntimeSettings.RetailDefaults,
                        EnbRenderTextureBackendCapabilities.ModernDefault, assets.Textures, decoded, null,
                        LiveLaneKind.Textured, out var lane, out error) ||
                    !TryCreateGpuResource(gl, lane, LiveLaneKind.Textured, 0, out var gpu, out error))
                    throw new InvalidDataException("Map frame GPU: " + error);
                if(key.Item5!=Matrix4x4.Identity)lane.Textured!.LightingPlan.CanonicalClipFromModel*=key.Item5;_mapDraws.Add((lane,gpu,original.RenderObjectName,highlight));
            }
            _mapKey = key;
        }
        return camera;
    }

    private void RenderMap(EnbGlApi gl, int fb, int width, int height, double scaling, EnbMapDraw p, bool galaxy)
    {
        if (UiLayout?.Visible(EnbUiGroup.Map)==false) return;
        var images=galaxy?_galaxyMapImages:_mapImages; var quads=galaxy?_galaxyMapQuads:_mapQuads;
        int imagesBefore=images.Count,quadsBefore=quads.Count,glyphsBefore=_mapGlyphDraws.Count;
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        RenderMapCore(gl,fb,width,height,scaling,p,images,quads);
        // A slow map frame (the galaxy map's first opening) names its cost for the session log.
        double ms=System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if(ms>=100)Volatile.Write(ref _slowMapRender,FormattableString.Invariant(
            $"{(galaxy?"galaxy":"sector")} map frame {ms:0} ms: {quads.Count-quadsBefore} new quad renderers ({quads.Count}), {images.Count-imagesBefore} new image renderers ({images.Count}), {_mapGlyphDraws.Count-glyphsBefore} new marker models"));
    }
    private string? _slowMapRender;
    /// <summary>The last slow map frame's account (taken once by the session log).</summary>
    internal string? TakeSlowMapRender()=>Interlocked.Exchange(ref _slowMapRender,null);

    private void RenderMapCore(EnbGlApi gl,int fb,int width,int height,double scaling,EnbMapDraw p,List<EnbOpenGlBackdropRenderer> images,List<EnbOpenGlBackdropRenderer> quads)
    {
        if ((int)Math.Round(p.Size.Width * scaling) != width || (int)Math.Round(p.Size.Height * scaling) != height ||
            _retailAssetsAccessor() is not { } assets) return;
        var camera=PrepareMapFrame(gl,width,height,p,assets);
        if (!p.Packets.IsEmpty)
        foreach (var d in _mapDraws)
        {
            if (d.Name == "RESIZE_UP" && p.Maximized || d.Name == "RESIZE_DOWN" && !p.Maximized ||
                d.Highlight && d.Name != p.Hovered) continue;
            SubmitGpuResource(gl,fb,width,height,d.Gpu,d.Lane,new(false,0,0,0),default,null,0);
        }
        int index = 0, quadIndex = 0;
        var contentCamera = camera;
        if (p.GalaxyCamera is {} galaxyCamera)
        {
            var r = p.ContentBounds;
            var viewportTransform = Matrix4x4.CreateScale((float)r.Width/640,(float)r.Height/480,1)*
                Matrix4x4.CreateTranslation((float)(r.Left+r.Right)/640-1,1-(float)(r.Top+r.Bottom)/480,0);
            contentCamera = galaxyCamera with { Viewport = new(width,height,1),
                Projection = galaxyCamera.Projection with { Matrix = galaxyCamera.Projection.Matrix*viewportTransform } };
        }
        if(LayoutClip(EnbUiGroup.Map)!=Matrix4x4.Identity)contentCamera=contentCamera with {Projection=contentCamera.Projection with {Matrix=contentCamera.Projection.Matrix*LayoutClip(EnbUiGroup.Map)}};
        if (!p.Markers.IsEmpty || !p.ContentImages.IsEmpty || !p.UnderlayImages.IsEmpty || !p.Route.IsEmpty || !p.WarpRoute.IsEmpty || p.ModelScene is not null || !p.Connections.IsEmpty)
        {
            var r=p.ContentBounds;
            ScissorMap(gl,r,width,height);
            gl.Enable(0x0C11); // GL_SCISSOR_TEST: partially visible icons must clip at the map edge.
            try
            {
                // Shade bands share edges: round each edge, not origin and size, so no seam opens.
                foreach (var image in p.UnderlayImages) DrawImage(image,edges:true);
                // Next Modern UI schematic galaxy map: its gate lines, under its sector boxes (no markers there).
                if (p.Markers.IsEmpty && p.GalaxyCamera is null && !p.Connections.IsEmpty)
                {
                    if (_mapRouteRenderer is null) { _mapRouteRenderer=new(); _mapRouteRenderer.Initialize(gl); }
                    foreach (var line in p.Connections) _mapRouteRenderer.RenderMapLine(gl,fb,width,height,MapPoint(line.From),MapPoint(line.To),line.Texture,line.Width*(float)(UiLayout?.Placement(EnbUiGroup.Map).Scale??1),line.Repeat,line.Offset);
                }
                if (p.Route.Length > 1 || p.WarpRoute.Length > 1)
                {
                    if (_mapRouteRenderer is null) { _mapRouteRenderer = new(); _mapRouteRenderer.Initialize(gl); }
                    for (int i = 1; i < p.Route.Length; i++)
                        _mapRouteRenderer.RenderMapLine(gl, fb, width, height, MapPoint(p.Route[i - 1]), MapPoint(p.Route[i]),lineWidth:(float)(.75*(UiLayout?.Placement(EnbUiGroup.Map).Scale??1)));
                    for (int i = 1; i < p.WarpRoute.Length; i++)
                        _mapRouteRenderer.RenderMapLine(gl, fb, width, height, MapPoint(p.WarpRoute[i - 1]), MapPoint(p.WarpRoute[i]),lineWidth:(float)(.75*(UiLayout?.Placement(EnbUiGroup.Map).Scale??1)),warp:true);
                }
                RenderMapModels(gl,fb,width,height,p);
                ScissorMap(gl,r,width,height);
                gl.Enable(0x0C11);
                // Dynamic system/text geometry submits immediately (00975E30,
                // 006D8270); authored glyphs defer by descending static bucket.
                // Screen-blended level15 dots must precede additive level10 glyphs.
                var galaxyDraws=new List<(int Level,Action Draw)>();
                if(p.GalaxyCamera is not null)
                {
                    gl.DepthMask(1); gl.Clear(GlDepthBufferBit);
                    foreach(var quad in p.GalaxyBackgrounds) DrawQuad(quad);
                    foreach(var quad in p.GalaxyLabels) DrawQuad(quad);
                }
                int markerIndex=0;
                foreach (var marker in p.Markers)
                {
                    if (p.GalaxyCamera is null && p.BackgroundMarkerCount>0 && markerIndex<=p.BackgroundMarkerCount) { gl.DepthMask(1); gl.Clear(GlDepthBufferBit); }
                    if (markerIndex++==p.BackgroundMarkerCount && p.GalaxyCamera is null)
                    {
                        foreach (var quad in p.GalaxyBackgrounds) DrawQuad(quad);
                        foreach (var quad in p.GalaxyLinks) DrawQuad(quad);
                        foreach (var image in p.BackgroundImages) DrawImage(image,true);
                        if (!p.Connections.IsEmpty)
                        {
                            if (_mapRouteRenderer is null) { _mapRouteRenderer=new(); _mapRouteRenderer.Initialize(gl); }
                            foreach (var line in p.Connections) _mapRouteRenderer.RenderMapLine(gl,fb,width,height,MapPoint(line.From),MapPoint(line.To),line.Texture,line.Width*(float)(UiLayout?.Placement(EnbUiGroup.Map).Scale??1),line.Repeat,line.Offset);
                        }
                    }
                    var glyphKey=(marker.Glyph,marker.Color,marker.PreserveMaterial);
                    if (!_mapGlyphDraws.TryGetValue(glyphKey,out var draws))
                    {
                        draws=[]; _mapGlyphDraws.Add(glyphKey,draws);
                        var decoded=new Dictionary<string,EnbTextureImage>();
                        foreach (var source in marker.Glyph.Packets)
                        {
                            var packet=marker.PreserveMaterial ? source : EnbMapGlyph.Tint(source,marker.Color);
                            if (!EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out var error) ||
                                !TryCompileLane(submission,contentCamera,EnbRenderTextureRuntimeSettings.RetailDefaults,
                                    EnbRenderTextureBackendCapabilities.ModernDefault,assets.Textures,decoded,null,LiveLaneKind.Textured,out var lane,out error) ||
                                !TryCreateGpuResource(gl,lane,LiveLaneKind.Textured,0,out var gpu,out error))
                                throw new InvalidDataException("Map marker GPU: "+error);
                            draws.Add((lane,gpu));
                        }
                    }
                    var transform=marker.WorldTransform ?? Matrix4x4.CreateScale(marker.Scale.X,marker.Scale.Y,1)*Matrix4x4.CreateRotationZ(marker.Heading)*Matrix4x4.CreateTranslation(marker.Position.X-320,240-marker.Position.Y,0);
                    foreach (var d in draws)
                    {
                        var previous=d.Lane.Textured!;
                        var submission=previous.Submission with { Packet=(previous.Submission.Packet).ForkTask().SetWorldTransform(previous.Submission.Packet.WorldTransform*transform) };
                        if (!EnbW3dProgrammableTextureLaneCompiler.TryUpdateFrame(previous,submission,contentCamera,out var updated,out var error))
                            throw new InvalidDataException("Map marker transform: "+error);
                        void DrawGlyph()=>SubmitGpuResource(gl,fb,width,height,d.Gpu,LiveLanePlan.From(updated),new(false,0,0,0),default,null,0);
                        if(p.GalaxyCamera is null) DrawGlyph();
                        else if(submission.Packet.OrderKey.StaticSort.Disposition==EnbW3dStaticSortDisposition.Immediate) DrawGlyph();
                        else if(submission.Packet.OrderKey.StaticSort.IsDeferred) galaxyDraws.Add((submission.Packet.OrderKey.StaticSort.RuntimeLevel,DrawGlyph));
                    }
                }
                foreach(var draw in galaxyDraws.OrderByDescending(d=>d.Level)) draw.Draw();
                if(p.GalaxyCamera is not null)
                    foreach(var quad in p.GalaxyLinks) DrawQuad(quad);
                foreach (var image in p.ContentImages) DrawImage(image);
            }
            finally { gl.Disable(0x0C11); }
        }
        foreach (var image in p.Images)
        {
            if(LayoutRect(EnbUiGroup.Map,image.Clip) is {} clip)
            {
                int left=(int)Math.Ceiling(clip.Left*scaling),right=(int)Math.Floor(clip.Right*scaling);
                int top=(int)Math.Ceiling(clip.Top*scaling),bottom=(int)Math.Floor(clip.Bottom*scaling);
                if(right<=left || bottom<=top)continue;
                gl.Enable(0x0C11);
                HudScissor(gl)(left,height-bottom,right-left,bottom-top);
            }
            try { DrawImage(image); }
            finally { if(image.Clip is not null)gl.Disable(0x0C11); }
        }
        if (!p.LegendMarkers.IsEmpty)
        {
            // Next Modern UI map legend: its marker symbols, in the window beside the map (not clipped to the map area).
            var legendCamera=camera;
            if(LayoutClip(EnbUiGroup.Map)!=Matrix4x4.Identity)legendCamera=legendCamera with {Projection=legendCamera.Projection with {Matrix=legendCamera.Projection.Matrix*LayoutClip(EnbUiGroup.Map)}};
            foreach (var marker in p.LegendMarkers) DrawLegendMarker(marker,legendCamera);
        }
        if (p.Help is { } help)
        {
            if (_mapHelpRenderer is null)
            {
                _mapHelpRenderer=CreateTooltipLayer(_retailAssetsAccessor,_fixedSessionIdAccessor);
                _mapHelpRenderer.InitializeGl(gl);
            }
            _mapHelpRenderer._tooltipPresentation=help;
            _mapHelpRenderer.RenderTooltip(gl,fb,width,height,scaling);
        }
        gl.Viewport(0,0,width,height);

        void DrawLegendMarker(EnbMapMarker marker,EnbResolvedRenderCamera legendCamera)
        {
            var glyphKey=(marker.Glyph,marker.Color,marker.PreserveMaterial);
            if (!_mapGlyphDraws.TryGetValue(glyphKey,out var draws))
            {
                draws=[]; _mapGlyphDraws.Add(glyphKey,draws);
                var decoded=new Dictionary<string,EnbTextureImage>();
                foreach (var source in marker.Glyph.Packets)
                {
                    var packet=marker.PreserveMaterial ? source : EnbMapGlyph.Tint(source,marker.Color);
                    if (!EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out var error) ||
                        !TryCompileLane(submission,legendCamera,EnbRenderTextureRuntimeSettings.RetailDefaults,
                            EnbRenderTextureBackendCapabilities.ModernDefault,assets.Textures,decoded,null,LiveLaneKind.Textured,out var lane,out error) ||
                        !TryCreateGpuResource(gl,lane,LiveLaneKind.Textured,0,out var gpu,out error))
                        throw new InvalidDataException("Map legend GPU: "+error);
                    draws.Add((lane,gpu));
                }
            }
            var transform=Matrix4x4.CreateScale(marker.Scale.X,marker.Scale.Y,1)*Matrix4x4.CreateRotationZ(marker.Heading)*Matrix4x4.CreateTranslation(marker.Position.X-320,240-marker.Position.Y,0);
            foreach (var d in draws)
            {
                var previous=d.Lane.Textured!;
                var submission=previous.Submission with { Packet=(previous.Submission.Packet).ForkTask().SetWorldTransform(previous.Submission.Packet.WorldTransform*transform) };
                if (!EnbW3dProgrammableTextureLaneCompiler.TryUpdateFrame(previous,submission,legendCamera,out var updated,out var error))
                    throw new InvalidDataException("Map legend transform: "+error);
                SubmitGpuResource(gl,fb,width,height,d.Gpu,LiveLanePlan.From(updated),new(false,0,0,0),default,null,0);
            }
        }
        Vector2 MapPoint(Vector2 point)=>(UiLayout?.Placement(EnbUiGroup.Map)??EnbUiPlacement.Identity).Apply(point,new(640,480));
        void DrawQuad(EnbMapQuad quad)
        {
            if (quads.Count <= quadIndex) quads.Add(new() { MapQuadMode = true });
            var renderer = quads[quadIndex++];
            if (!renderer.IsInitialized) renderer.Initialize(gl);
            renderer.RenderMapQuad(gl,fb,width,height,quad,contentCamera);
        }

        void DrawImage(EnbUiTextDraw image,bool additive=false,bool edges=false)
        {
            if (images.Count <= index) images.Add(new());
            var renderer=images[index++]; var r=LayoutRect(EnbUiGroup.Map,image.Bounds);
            if (!renderer.IsInitialized) renderer.Initialize(gl);
            if (edges)
            {
                int left=(int)Math.Round(r.X*scaling),right=(int)Math.Round(r.Right*scaling),top=(int)Math.Round(r.Y*scaling),bottom=(int)Math.Round(r.Bottom*scaling);
                if (right>left && bottom>top) renderer.RenderOverlay(gl,fb,left,height-bottom,right-left,bottom-top,image.Raster,additive,opacity:image.Opacity);
                return;
            }
            renderer.RenderOverlay(gl,fb,(int)Math.Round(r.X*scaling),height-(int)Math.Round(r.Bottom*scaling),
                Math.Max(1,(int)Math.Round(r.Width*scaling)),Math.Max(1,(int)Math.Round(r.Height*scaling)),image.Raster,additive,opacity:image.Opacity);
        }
    }

    private readonly EnbSceneFrame _mapModelScene=new();
    private readonly List<EnbSampledSceneObject> _mapModelStorage=[];
    private readonly List<IEnbRenderObject> _mapModelObjects=[];
    private void RenderMapModels(EnbGlApi gl,int fb,int width,int height,EnbMapDraw p)
    {
        if(p.ModelScene is not {} sourceScene || sourceScene.Objects.Count==0 || p.WorldScale<=0)return;
        RenderMapPlanets(gl,fb,width,height,p);
        _mapModelScene.AdoptMetadata(sourceScene);
        _mapModelObjects.Clear();
        var r=p.ContentBounds;
        foreach(var sourceObject in sourceScene.Objects)
        {
            if(sourceObject.ObjectType==3)continue;
            int slot=_mapModelObjects.Count;
            if(slot==_mapModelStorage.Count)_mapModelStorage.Add(new());
            var obj=_mapModelStorage[slot];obj.AdoptViewObject(sourceObject);
            if(p.ModelPositions.TryGetValue(obj.GameId,out var at) && obj.Spatial is {} spatial)
            {
                var pose=obj.Pose;var t=pose.Sample;
                var position=new Vector3(p.WorldCenter.X+(at.X-(float)(r.X+r.Width*.5))/p.WorldScale,
                    p.WorldCenter.Y-(at.Y-(float)(r.Y+r.Height*.5))/p.WorldScale,t.Position.Z);
                var provider=t.ProviderTransform;
                if(provider is {} matrix){matrix.Translation+=position-t.Position;provider=matrix;}
                obj.Pose=pose with {Sample=t with {Position=position,ProviderTransform=provider}};
            }
            _mapModelObjects.Add(obj);
        }
        _mapModelScene.Objects=_mapModelObjects;
        IEnbSceneFrame scene=_mapModelScene;
        if(scene.Objects.Count==0)return;
        int x=(int)Math.Ceiling(r.Left*width/640), y=height-(int)Math.Floor(r.Bottom*height/480);
        int w=(int)Math.Floor(r.Right*width/640)-x, h=(int)Math.Floor(r.Bottom*height/480)-(int)Math.Ceiling(r.Top*height/480);
        if (w<1 || h<1) return;
        if (_mapModelRenderer is null)
        {
            _mapModelRenderer=CreatePreviewModelRenderer(gl);MapModelProgramInitializations++;
        }
        float top=scene.Objects.Max(o=>o.Spatial!.Transform.Position.Z+EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(o));
        float bottom=scene.Objects.Min(o=>o.Spatial!.Transform.Position.Z-EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(o));
        var orientation=Quaternion.CreateFromRotationMatrix(new Matrix4x4(0,0,-1,0,-1,0,0,0,0,1,0,0,0,0,0,1));
        //The framebuffer covers whole physical pixels inside the authored
        //content rectangle. Preserve the global logical-to-pixel scale instead
        //of stretching that rectangle to the rounded framebuffer dimensions.
        double sx=width/640d,sy=height/480d;
        var center=p.WorldCenter+new Vector2((float)(((x+w*.5)/sx-(r.X+r.Width*.5))/p.WorldScale),
            (float)(((r.Y+r.Height*.5)-(height-y-h*.5)/sy)/p.WorldScale));
        if (!EnbResolvedRenderCamera.TryCreateOrthographic(new(center,top+1),orientation,new(w,h,1),
            w/(sx*2*p.WorldScale),h/(sy*2*p.WorldScale),new(.1f,Math.Max(2,top-bottom+2)),out var camera)) return;
        var source=MapWorldRenderer;
        var planet=source?.PlanetGpuPresentation ?? LivePlanetGpuPresentation.Waiting;
        var light=new EnbSceneLightSnapshot(0,0,Vector3.One,Vector3.One,new(.3f),camera.Position,camera.Basis.Forward,
            1_000_000,1,1,0,0,MathF.PI/4,MathF.PI/4);
        _mapModelScene.Lighting=new(0x00333333,[light]);_mapModelScene.Effects=[];_mapModelScene.StationAvatars=[];
        var frame=new LiveFrame(scene,camera,false,scene.ClientTime,System.Diagnostics.Stopwatch.GetTimestamp(),null,0,
            planet.DayNightPhasesByRenderIdentity,planet.MapperStartMillisecondsByRenderIdentity)
            { RslObjectUpdateOverrides=source?._frameRslObjectUpdates };
        int modelFb=_mapModelFramebuffer.BeginPlaced(gl,fb,x,y,w,h,width,height,UiLayout?.Placement(EnbUiGroup.Map)??EnbUiPlacement.Identity);
        try { _mapModelRenderer.RenderFrame(gl,modelFb,w,h,frame); }
        finally { _mapModelFramebuffer.EndPlaced(gl,fb,x,y,width,height,UiLayout?.Placement(EnbUiGroup.Map)??EnbUiPlacement.Identity); gl.Viewport(0,0,width,height); }
    }
}
