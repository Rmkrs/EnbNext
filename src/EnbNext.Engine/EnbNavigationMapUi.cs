using Point = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>Input-only overlay; all frame, marker and font presentation is handed to the GPU.</summary>
internal sealed partial class EnbNavigationMapUi : IDisposable
{
    private static readonly string[] Buttons = ["EXIT","RESIZE_BTTN","QUESTION_BTTN","CENTER_BTTN","XYZ_BTTN","SECTOR_BTTN","LABEL_BTTN",EnbAuthoredMapFrame.MarkerButtonName];
    private readonly Func<uint,CancellationToken,Task<bool>> _select;
    private readonly Func<uint?> _session;
    private EnbRetailAssetBundle? _assets;
    private uint? _ownerSession;
    private ulong? _owner;
    private uint? _sector;
    private IEnbSceneFrame? _scene;
    private ImmutableArray<uint> _route = [];
    private EnbNext.Net7.Gameplay.Net7WarpRouteSnapshot? _capturedRouteOwner;
    private uint? _observedTarget, _routeTarget;
    private ulong _observedTargetSequence;
    private readonly EnbNavigationMapRouteAnimation _routeAnimation=new();
    private uint _routeElapsed;
    private uint _labelElapsed;
    private readonly Dictionary<ulong,float> _rescueLabelAlpha=[];
    private ulong _labelWorldRegistryRevision;
    private int _labelMode=-1;
    private bool _labelsInvalidated;
    public void InvalidateLabels()=>_labelsInvalidated=true;
    private readonly Dictionary<string,EnbRgb> _labelColors=new(StringComparer.OrdinalIgnoreCase);
    private uint _destinationId;
    private string _destinationText="";
    private long _selectionRequest;
    private EnbAuthoredMapFrame? _frame;
    private EnbNavigationMapState? _state;
    private EnbNavigationMapProjection _projection = new();
    private readonly Dictionary<string,EnbMapGlyph> _glyphs = [];
    private readonly HashSet<string> _missingGlyphs = [];
    private readonly Dictionary<string,EnbRetailFontFace> _fonts = [];
    private readonly Dictionary<string,EnbRetailText> _text = [];
    private EnbRetailIniDocument? _strings;
    private EnbInterfaceLayoutCatalog? _layout;
    private EnbInterfaceLayoutCatalog? _radarLayout;
    private EnbRetailScannerTooltipDefinition? _helpDefinition;
    private EnbAuthoredTooltipFrame? _helpFrame;
    internal string? CursorResource { get; private set; }
    internal EnbUiTooltipDraw? HelpPresentation { get; private set; }
    private readonly EnbSceneFrame _mapScene=new();
    private readonly List<EnbSampledSceneObject> _modelStorage=[];
    private readonly List<IEnbRenderObject> _modelObjects=[];
    private readonly Dictionary<uint,Rect> _labelBounds=[];
    private EnbRetailScannerTooltipDefinition? _tooltipDefinition;
    private EnbAuthoredTooltipFrame? _tooltipFrame;
    private readonly EnbCurrentRetailTooltipDelay _dwell = new();
    private Size _size;
    internal EnbUiLayoutProfiles? UiLayout {get;set;}
    internal bool LayoutPreview=>!GalaxyWindow&&UiLayout?.Preview(EnbUiGroup.Map)==true;
    internal Rect LayoutBounds=>_frame is null?default:Logical(Background);
    private bool _layoutPublished;
    private double _scaling=1;
    private long _lastTime;
    private Point _pointer, _pressPoint, _lastPointer;
    private string? _hovered, _pressedButton;
    private uint? _hoveredObject, _pressedObject;
    private bool _right, _dragged, _interactive, _help, _gesture, _openedOnce;
    private bool _captured;
    private readonly Dictionary<uint,(IEnbRenderObject Object,Rect Bounds)> _markers = [];
    public bool IsOpen { get; private set; }
    public Func<int, bool>? BridgePanelRequested { get; set; }
    public event Action<string>? InteractionSoundRequested;
    public string? Error { get; private set; }
    public string Status => Error ?? (IsOpen ? $"open · {_markers.Count} markers · zoom {_projection.Zoom:0.00} · labels {_state?.LabelMode} · coords {_state?.CoordinateMode}" : "closed · resolved Nav Map binding");
    public EnbMapDraw? Presentation { get; private set; }
    public EnbMapDraw? Preparation { get; private set; }
    public event Action<EnbUiTooltipDraw?>? TooltipChanged;
    public event Action? Changed;

    public EnbNavigationMapUi(Func<uint,CancellationToken,Task<bool>> select,Func<uint?> session)
    {
        _select=select; _session=session;
        WireNoteEditor();
    }

    internal event Action<bool>? VisibilityChanged;
    internal event Action<bool>? CaptureRequested;
    internal event Action<Rect>? HitBoundsChanged;
    internal event Action? ClearCursorRequested;
    internal event Action? ClearCursorAssetsRequested;
    internal Func<string,bool>? CursorRequested {get;set;}

    public bool Toggle()
    {
        if (!_interactive || _frame is null) return false;
        if (IsOpen) Close();
        else
        {
            if (BridgePanelRequested?.Invoke(3) == false) return false;
            // The sector census can finish arriving after flight first becomes ready.
            // Fit the first opening to the current census; every opening locks back onto the ship.
            if (!_galaxy)
            {
                _projection.FitMargin=_modern ? .06f : 0; _projection.Bounded=_modern;
                if (!_openedOnce && _scene is not null) _projection.Fit(_scene.Objects);
                CenterOnShip();
            }
            else { AdvanceGalaxyModels(); FrameGalaxyHome(true); }
            _openedOnce=true; IsOpen=true; Raise(); VisibilityChanged?.Invoke(true); _lastTime=Environment.TickCount64; Publish();
        }
        return true;
    }
    public bool Close()
    {
        if (IsOpen && BridgePanelRequested?.Invoke(0) == false) return true;
        return CloseForPopup();
    }
    internal bool CloseForPopup()
    {
        if (!IsOpen) return false;
        IsOpen=false; VisibilityChanged?.Invoke(false); EndModernFind(); _modernFindOpen=false; if (_galaxy) ClearGalaxyFocus(); _help=false; HelpPresentation=null; CancelPointer(); ClearHover();
        RefreshDestinationLabel();
        Presentation=null; Changed?.Invoke(); return true;
    }
    public void Suspend() { NoteEditor.Close(); _addingMarker=false; _hoveredNote=null; _noteBounds.Clear(); if(!SharesGuidance)Guidance.Suspend(); CloseForPopup(); ResetGalaxyView(); _interactive=false; _scene=null; _projection=new(); _sectorProjection=new(); _galaxyProjection=new(); _galaxy=false; _openedOnce=false; _markers.Clear(); _route=[]; _selectionRequest++; }
    public void CancelPointer()
    {
        _captured=false; _pressedButton=null; _pressedObject=null; _gesture=false;
        // Remove the map override so the world cursor is inherited again.
        // A local null overrides inheritance and selects the OS default arrow.
        ClearCursorRequested?.Invoke(); CursorResource=null;
        CaptureRequested?.Invoke(false);
    }
    public bool BlocksFlight(Point point) => IsOpen && _frame is not null && Background.Contains(Design(point));
    private void ZoomMap(float amount)
    {
        if (Schematic) ZoomSchematic(amount);
        else if (_galaxy) _galaxyCamera.Zoom(amount,_galaxyRadius);
        else _projection.StepZoom(amount);
    }
    public bool ZoomAction(string? action)
    {
        if (!IsOpen || action is not ("Zoom Map In" or "Zoom Map Out")) return false;
        ZoomMap(action=="Zoom Map In" ? .1f : -.1f); Publish(); return true;
    }

    public void Update(IEnbSceneFrame scene,EnbRetailAssetBundle? assets,Size size,double scaling,bool interactive)
    {
        if(_mapFontSession!=scene.ClientSoundStreamId) { _mapFontSession=scene.ClientSoundStreamId; ResetMapFonts(); }
        RoutePlannerEnabled = assets?.CharacterInterface.RoutePlanner == true;
        if (!RoutePlannerEnabled && Guidance.Destination.HasValue) Guidance.Choose(null);
        if(!SharesGuidance)Guidance.Update(GalaxyMapAccessor?.Invoke(),scene);
        var ship=scene.Objects.FirstOrDefault(o=>o.IsLocalShip);
        bool ownerChanged=_scene is null || _ownerSession!=_session() || _owner!=ship?.RenderIdentity || _sector!=scene.Environment?.SectorNum;
        if (ownerChanged)
        {
            CloseForPopup(); _projection=new(); _ownerSession=_session(); _owner=ship?.RenderIdentity; _sector=scene.Environment?.SectorNum;
            _sectorProjection=new(); _galaxyProjection=new(); _galaxy=false;
            _state=null; _openedOnce=false; ResetGalaxyView();
            _route=[]; _selectionRequest++; _destinationId=0; _destinationText="";
            foreach (var line in _text.Values) line.Clear();
            _text.Clear(); ResetLabelOwners();
        }
        uint? selected=scene.Hud.HasLocalTargetSelection ? scene.Hud.LocalSelectedTargetId : scene.Hud.CurrentTargetId;
        var captured=scene.Hud.WarpRoute;
        _scene=scene; _size=size; _scaling=scaling; _interactive=interactive;
        if(ownerChanged) { _observedTarget=null; _routeTarget=null; _capturedRouteOwner=null; _observedTargetSequence=0; }
        // 007BD980 copies the newly captured route into the selected-route owner.
        // 007C0910 renders both owners, independently of cockpit warp state.
        bool newCapture=captured is not null && (_capturedRouteOwner is null ||
            captured.CaptureSequence!=_capturedRouteOwner.CaptureSequence || !captured.ObjectIds.Equals(_capturedRouteOwner.ObjectIds));
        bool targetChanged=selected!=_observedTarget || scene.Hud.TargetSelectionSequence!=_observedTargetSequence;
        void ApplyTarget()
        {
            if(!targetChanged)return;
            bool coalesced=selected==_observedTarget && scene.Hud.TargetSelectionSequence!=_observedTargetSequence;
            _observedTarget=selected; _observedTargetSequence=scene.Hud.TargetSelectionSequence;
            SetRouteTarget(selected,coalesced);
        }
        bool targetFirst=newCapture && scene.Hud.TargetSelectionSequence<=captured!.CaptureSequence;
        if(targetFirst)ApplyTarget();
        if(newCapture)
        {
            _route=captured!.ObjectIds; _routeAnimation.Reset(); _routeElapsed=0;
        }
        _capturedRouteOwner=captured;
        if(!targetFirst)ApplyTarget();
        if(_routeTarget is uint routeTarget && !scene.Objects.Any(o=>o.GameId==routeTarget)) SetRouteTarget(null);
        if (!ReferenceEquals(_assets,assets))
        {
            CloseForPopup(); ClearAssets(); _assets=assets; NoteEditor.Assets=assets;
            if (assets is not null)
            try
            {
                _frame=new(assets);
                _layout=EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fa_tsi26a.ini"));
                _radarLayout=EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fc_radar.ini"));
                _labelColors.Clear();
                foreach(string file in new[]{"fontart.ini","fontcode.ini"})
                    foreach(var color in EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,file)).EnumerateSections().Select(s=>s.Value))
                        if(color.GetValueOrDefault("ColorID") is {} id && color.GetValueOrDefault("RGB")?.Split(',') is {Length:3} rgb)
                            _labelColors[id]=new(byte.Parse(rgb[0])/255f,byte.Parse(rgb[1])/255f,byte.Parse(rgb[2])/255f);
                _strings=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"tooltip.ini"));
                _tooltipDefinition=EnbRetailScannerTooltipDefinition.Load(assets.Paths.ClientIniDirectory);
                if (_tooltipDefinition is { } def) _tooltipFrame=EnbAuthoredTooltipFrame.Load(assets,def);
                string helpFont=_layout.TryResolve("STARMAP_HOW_TO",out var helpStyle) ? helpStyle.Properties.GetValueOrDefault("FontType","FONT_SMALL") : "FONT_SMALL";
                _helpDefinition=EnbRetailScannerTooltipDefinition.Parse(EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fc_default.ini")),
                    _strings,EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"dynawin.ini")),"STARMAP_HOW_TO",helpFont);
                if (_helpDefinition is { } helpDef) _helpFrame=EnbAuthoredTooltipFrame.Load(assets,helpDef);
                LoadMapHelpEffects();
                // Decode immutable marker assets while the normal HUD prepares,
                // without opening the map or advancing its retained animation.
                foreach(string icon in Enumerable.Range(0,256).Select(type=>EnbNavigationMapProjection.Icon((byte)type,false))
                    .Append(EnbNavigationMapProjection.Icon(0,true)).Distinct())
                {
                    try { _glyphs[icon]=EnbMapGlyph.Load(assets,icon); }
                    catch(Exception ex) when(ex is InvalidDataException or IOException)
                    { /* Optional warmup: report an unavailable glyph only if the map actually requests it. */ }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            { Error=ex.Message; _frame=null; }
        }
        if (_frame is null || assets is null) return;
        if(_fontWarmSize!=(_size,_scaling))
        {
            foreach(string font in _layout!.Definitions.Concat(_radarLayout!.Definitions)
                .Select(d=>d.Properties.GetValueOrDefault("FontType")).OfType<string>().Distinct())
                Font(font);
            _fontWarmSize=(_size,_scaling);
        }
        _state ??= new(assets.ResolvePlayerOptions(_session()).Options);
        if(!IsOpen)PrepareMapFontStyles();
        if(!IsOpen && !_mapTextPrepared) { _preparedMapFonts=MapFonts(prepareOnly:true); _mapTextPrepared=true; }
        if(Preparation is not {} prepared || !ReferenceEquals(prepared.Frame,_frame) ||
            prepared.AnimationFrame!=_state.Frame || prepared.Size!=_size || prepared.ModelOwner!=(_ownerSession,_owner,_sector))
            Preparation=new(_frame,_state.Frame,_state.Maximized,(_size),null,_preparedMapFonts)
            { ModelOwner=(_ownerSession,_owner,_sector),ContentBounds=Content,Markers=_glyphs.Values.Select(glyph=>new EnbMapMarker(glyph,Vector2.Zero,0,new(1,1,1))).ToImmutableArray() };
        if (!interactive) { CloseForPopup(); return; }
        // Next Modern UI: the sector map stays inside its sector, fitted with a small margin.
        _projection.FitMargin=_modern ? .06f : 0; _projection.Bounded=_modern && !_galaxy;
        if (!_projection.IsInitialized) _projection.Fit(scene.Objects);
        LayoutModern();
        ObserveLabelOwners();
        if (!IsOpen){if(LayoutPreview){Publish();_layoutPublished=true;}else if(_layoutPublished){Presentation=null;_layoutPublished=false;Changed?.Invoke();}return;}
        long now=Environment.TickCount64;
        long elapsed=Math.Max(0,now-_lastTime);
        _state.Advance(elapsed); _lastTime=now;
        _mapFontTime+=elapsed;
        if(_help)_mapHelpTime[_galaxy?1:0]+=elapsed;
        _routeElapsed=_galaxy ? 0 : (uint)Math.Min(uint.MaxValue,elapsed);
        _labelElapsed=_routeElapsed;
        if (!_galaxy)
        {
            _frame.Evaluate(_state.Frame);
            _projection.BeginRefresh(Viewport);
            _projection.Follow(scene.Objects);
        }
        else { _galaxyRenderElapsed=(uint)Math.Min(uint.MaxValue,elapsed); AdvanceGalaxyModels(); _galaxyCamera.Advance((uint)Math.Min(uint.MaxValue,elapsed)); if (_galaxySystemPulse!=0) _galaxySystemPulse+=unchecked((uint)elapsed); }
        Publish();
    }

    private Point Design(Point p){p=UiLayout?.Inverse(EnbUiGroup.Map,p)??p;return new(p.X*640/_size.Width,p.Y*480/_size.Height);}
    private static double Distance(Point a,Point b)=>Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
    private Point Logical(Point p)=>new(p.X*_size.Width/640,p.Y*_size.Height/480);

    /// <summary>
    /// Next Modern UI: a marker's name label stays inside the map. Beside an edge it moves inward; above the top edge it
    /// goes under its marker instead. The map itself (zoom and position) is not changed for it.
    /// </summary>
    private Rect KeepLabelInView(Rect label,double markerBottom)
    {
        var area=Logical(Content);
        double x=Math.Clamp(label.X,area.X,Math.Max(area.X,area.Right-label.Width));
        double y=label.Y<area.Y ? markerBottom : label.Y;
        y=Math.Clamp(y,area.Y,Math.Max(area.Y,area.Bottom-label.Height));
        return new(x,y,label.Width,label.Height);
    }
    private Rect Logical(Rect r)=>new(Logical(r.TopLeft),Logical(r.BottomRight));
    private Vector2 Viewport=>new((float)Content.Width,(float)Content.Height);
    private Vector3 WorldAt(Point logical)
    {
        var p=Design(logical)-Content.TopLeft;
        return _projection.Unproject(new((float)p.X,(float)p.Y),Viewport);
    }
    private void Locate(Point p)
    {
        _pointer=p;
        var d=Design(p);
        string? button=_frame is null ? null : _modern ? ModernHit(p) : Buttons.FirstOrDefault(b=>_frame.Contains(b,d));
        uint? obj=button is null && Content.Contains(d)
            ? _markers.Where(m=>m.Value.Bounds.Contains(d) || _labelBounds.TryGetValue(m.Key,out var label) && label.Contains(UiLayout?.Inverse(EnbUiGroup.Map,p)??p)).OrderBy(m=>Distance(m.Value.Bounds.Center,d)).Select(m=>(uint?)m.Key).FirstOrDefault() : null;
        bool guidance=!_help && _guidanceBounds.Contains(p);
        if(guidance){button=null;obj=null;}
        Guid? note=button is null && !guidance && Content.Contains(d) ? NoteAt(d) : null;
        if(note is not null)obj=null;
        if (_guidanceHovered!=guidance || _hovered!=button || _hoveredObject!=obj) { _dwell.Reset(); TooltipChanged?.Invoke(null); }
        _guidanceHovered=guidance; _hovered=button; _hoveredObject=obj; _hoveredNote=note;
    }
    internal void Begin(Point point,bool right)
    {
        if (!BlocksFlight(point)) return;
        _guidancePressed=!_help && _guidanceBounds.Contains(point);
        Locate(point); _pressPoint=_lastPointer=point; _pressedButton=_hovered; _pressedObject=_hoveredObject;
        _right=right; _dragged=false; _gesture=true; TooltipChanged?.Invoke(null);
        if(_modern)Raise();
        if(_pressedButton=="mmap/title")_modernDragOrigin=_modernWindow.TopLeft;
        if(_pressedButton=="mmap/resize")_modernResizeOrigin=new(_modernMapRect.Width,_modernMapRect.Height);
        if(_pressedButton!="mmap/find")EndModernFind();
        SetMapPressCursor(right);
    }
    internal void Move(Point point) => MoveWithModifiers(point,false);
    internal void MoveWithModifiers(Point point,bool control)
    {
        if (!IsOpen) return;
        if(_gesture && _pressedButton=="mmap/title") { DragModern(point); Locate(point); Publish(); return; }
        if(_gesture && _pressedButton=="mmap/resize") { ResizeModern(point); Locate(point); Publish(); return; }
        if(_gesture && _guidancePressed) return;
        if(_gesture&&_pressedButton is null&&_dragged&&TryWrapMapPointer(point,out var wrapped))
        { _lastPointer=wrapped;Locate(wrapped);Publish();return; }
        if (Schematic && _gesture && _pressedButton is null)
        {
            // The schematic galaxy map pans with a drag and zooms with a right drag.
            double dx=point.X-_lastPointer.X, dy=point.Y-_lastPointer.Y;
            if (dx!=0 || dy!=0)
            {
                if (_dragged) { if (_right) ZoomSchematic((float)(-dy/Math.Max(1,_modernContentLogical.Height))); else PanSchematic(dx,dy); _lastPointer=point; }
                else { _dragged=true; if(!_right)SetMapCursor("c_handgrab.tga"); }
            }
            Locate(point); Publish(); return;
        }
        if (_galaxy && _gesture && _pressedButton is null)
        {
            var delta=GalaxyNormalized(_lastPointer)-GalaxyNormalized(point);
            if (delta!=Vector2.Zero)
            {
                if (_dragged)
                {
                    if (control) _galaxyCamera.Rotate(delta);
                    else if (_right) { _galaxyCamera.Zoom(delta.Y,_galaxyRadius); SetMapCursor(delta.Y>0 ? "c_zoomin.tga" : "c_zoomout.tga"); }
                    else _galaxyCamera.Pan(delta,_galaxyMinimum,_galaxyMaximum);
                    _lastPointer=point;
                }
                else { _dragged=true;if(control||!_right)SetMapCursor("c_handgrab.tga"); }
            }
            Locate(point); Publish(); return;
        }
        if (_gesture && _pressedButton is null)
        {
            var d=Design(point)-Design(_lastPointer);
            if (d!=default)
            {
                // 007B4C50/007B4D80: first nonzero motion arms the gesture
                // without advancing its anchor. Subsequent motion consumes
                // displacement normalized by this PIP's viewport.
                if (_dragged)
                {
                    if (_right)
                    {
                        SetMapCursor(d.Y<0 ? "c_zoomin.tga" : "c_zoomout.tga");
                        _projection.StepZoom((float)-d.Y/Viewport.Y);
                    }
                    else _projection.Pan(new((float)d.X,(float)d.Y),Viewport);
                    _lastPointer=point;
                }
                else { _dragged=true;if(!_right)SetMapCursor("c_handgrab.tga"); }
            }
            Locate(point); Publish(); return;
        }
        _lastPointer=point; Locate(point); Publish();
    }
    internal void End(Point point)=>EndMapButton(point,_right,false);
    internal void EndMapButton(Point point,bool right,bool retainCapture)
    {
        if (!_gesture) return;
        Locate(point);
        string? button=_pressedButton; uint? obj=_hoveredObject;
        bool dragged=_dragged;
        if(button=="mmap/title" && dragged && ModernMapPosition is {} moved) ModernMapMoved?.Invoke(moved);
        if(button=="mmap/resize" && dragged && ModernMapSize is {} resized) { ModernMapResized?.Invoke(resized); if(ModernMapPosition is {} kept) ModernMapMoved?.Invoke(kept); }
        if(retainCapture){_dragged=false;_right=!right;ClearCursorRequested?.Invoke();CursorResource=null;}
        else CancelPointer();
        if (!IsOpen || !BlocksFlight(point)) return;
        if (!dragged && _guidancePressed)
        {
            _guidancePressed=false;
            if(right && _guidanceBounds.Contains(point)) { Guidance.Choose(null); }
            Publish(); return;
        }
        _guidancePressed=false;
        if (!dragged && button is not null && button==_hovered) Activate(button,right);
        else if (!dragged && button is null)
        {
            if (Schematic) ClickSchematic(right);
            else if (_galaxy) ClickGalaxy(right);
            else if (NoteClick(point,right)) { }
            else if (right)
            {
                if (obj is uint id && _markers.TryGetValue(id,out var marker)) _projection.CenterAt(marker.Object.Spatial!.Transform.Position,id);
                else _projection.CenterAt(WorldAt(point));
            }
            else if (obj is uint id && _markers.TryGetValue(id,out var marker) && !marker.Object.IsLocalShip)
                _=Select(id,_owner);
        }
        Publish();
    }
    private async Task Select(uint id,ulong? owner)
    {
        if (!_interactive || owner!=_owner) return;
        long request=++_selectionRequest;
        try
        {
            bool accepted=await _select(id,CancellationToken.None);
            if (accepted && request==_selectionRequest && owner==_owner && _interactive && _scene is not null)
            {
                SetRouteTarget(id);
                Publish();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or IOException) { }
    }
    private void SetRouteTarget(uint? id,bool force=false)
    {
        if(_scene is null || !force && id==_routeTarget)return;
        _routeTarget=id;
        // 007BD6E0/00846940: selecting a remaining warp node clears the preview;
        // selecting another object builds a separate route, even during warp.
        var warp=_scene.Hud.WarpRoute;
        int first=(int)(warp?.ReportedIndex ?? 0);
        bool onWarp=warp is not null && warp.ObjectIds.Skip(first).Contains(id.GetValueOrDefault());
        _route=id is uint target && !onWarp ? EnbNavigationRoutePlanner.BuildPreview(_scene,target) : [];
        _routeAnimation.Reset(); _routeElapsed=0;
    }
    internal void Activate(string button,bool right)
    {
        if (_state is null || _scene is null) return;
        if (button.StartsWith("mmap/",StringComparison.Ordinal)) { ActivateModern(button); return; }
        switch(button)
        {
            case "EXIT": if(Close())InteractionSoundRequested?.Invoke("TextClick03.wav"); break;
            case "RESIZE_BTTN": _help=false; HelpPresentation=null; _state.ToggleSize(); break;
            case "SECTOR_BTTN": SwitchMapMode(); break;
            case "XYZ_BTTN": if (_galaxy) _galaxyGrid=!_galaxyGrid; else _state.CycleCoordinates(); break;
            case "LABEL_BTTN": if (_galaxy) _ = RequestGalaxyMapToggleAsync(); else _state.CycleLabels(); break;
            case "CENTER_BTTN":
                if (_galaxy) FitGalaxy();
                else CenterOnShip(right);
                break;
            case "QUESTION_BTTN": _help=!_help; break;
            case EnbAuthoredMapFrame.MarkerButtonName: ToggleAddMarker(); break;
        }
    }

    // 007C0910 -> 007C1C50: hovered/selected marker, mouse, locked object or center.
    private EnbRetailMapCoordinates? ResolveCoordinates()
    {
        if(_scene is null || _frame is null || _state is null)return null;
        uint? selected=_scene.Hud.HasLocalTargetSelection ? _scene.Hud.LocalSelectedTargetId : _scene.Hud.CurrentTargetId;
        Vector3? hover=_hoveredObject is uint id && _markers.TryGetValue(id,out var hit) ? hit.Object.Spatial?.Transform.Position : null;
        Vector3? locked=_scene.Objects.FirstOrDefault(o=>o.GameId==_projection.LockedObjectId)?.Spatial?.Transform.Position;
        var pointer=Design(_pointer)-Content.TopLeft;
        return EnbRetailMapCoordinates.Resolve(_state.CoordinateMode,Content.Contains(Design(_pointer)),
            _hoveredObject!=selected ? hover : null,_hoveredObject==selected ? hover : null,locked,
            new((float)(pointer.X/Viewport.X),(float)(pointer.Y/Viewport.Y)),Viewport,_projection.ScaledCenter,_projection.Scale(Viewport));
    }

    // 007C91A0 -> 007C0EE0: route destination, Name only, refreshed on ID change.
    private void RefreshDestinationLabel()
    {
        bool active=IsOpen && !_galaxy && _scene?.ActiveStarbaseId is null;
        uint destination=active ? _scene?.Hud.WarpRoute?.ObjectIds.LastOrDefault() ?? 0 : 0;
        if(active && destination==0) destination=_route.LastOrDefault();
        if(destination==_destinationId)return;
        _destinationId=destination;
        var obj=active ? _scene?.Objects.FirstOrDefault(o=>o.GameId==destination) : null;
        if(obj is null) { _destinationText=""; return; }
        string? name=obj.DisplayNameProperties is { } properties ? properties.Name : obj.Name;
        int nul=name?.IndexOf('\0') ?? -1;
        _destinationText="Dest: "+(name is null ? "?" : nul<0 ? name : name[..nul]);
    }

    internal string LastPublishProfile { get; private set; }="";
    private void Publish()
    {
        if ((!IsOpen&&!LayoutPreview) || _frame is null || _state is null || _scene is null || _assets is null || _size.Width<=0 || _size.Height<=0) return;
        long start=System.Diagnostics.Stopwatch.GetTimestamp();
        _frame.MarkerButton=NotesActive && !_modern;
        LayoutModern();
        _frame.Evaluate(_state.Frame);
        var panel=Logical(Background);
        HitBoundsChanged?.Invoke(UiLayout?.Apply(EnbUiGroup.Map,panel)??panel);
        RefreshDestinationLabel();
        var frameFonts=MapFonts();
        double fontsMs=System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (_galaxy) { PublishGalaxyTimed(); return; }
        var images=ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        var contentImages=ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        var usedText=new HashSet<string>();
        RefreshNormalLabels();
        var markers=ImmutableArray.CreateBuilder<EnbMapMarker>();
        var models=_modelObjects;models.Clear();
        var modelPositions=ImmutableDictionary.CreateBuilder<uint,Vector2>();
        int displayWidth=(int)(_size.Width*_scaling),displayHeight=(int)(_size.Height*_scaling);
        float mapScale=_projection.Scale(Viewport);
        var scaledCenter=_projection.ScaledCenter;
        _markers.Clear();
        _labelBounds.Clear();
        var local=_scene.Objects.FirstOrDefault(o=>o.IsLocalShip);
        uint? selected=_scene.Hud.HasLocalTargetSelection ? _scene.Hud.LocalSelectedTargetId : _scene.Hud.CurrentTargetId;
        if (!_glyphs.ContainsKey("m_pc.w3d")) _glyphs["m_pc.w3d"]=EnbMapGlyph.Load(_assets,"m_pc.w3d");
        foreach (var obj in _scene.ActiveStarbaseId is null ? _scene.Objects : [])
        {
            bool group=_scene.Hud.GroupMemberIds.Contains(obj.GameId);
            if (obj.Spatial?.Transform is not { } t || !EnbNavigationMapProjection.Finite(t.Position) ||
                !MapAdmits(obj)) continue;
            _projection.ObservePresentedObject(obj.RenderIdentity,t.Position,EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(obj));
            var placement=EnbRetailNavigationMapPlacement.Resolve(new(t.Position.X,t.Position.Y),mapScale,scaledCenter,
                Viewport,displayWidth,displayHeight);
            var position=placement.RenderPoint(scaledCenter,Viewport,displayWidth,displayHeight);
            var point=Content.TopLeft+new Point(position.X,position.Y);
            var hitPosition=placement.HitNormalized*Viewport;
            var hitPoint=Content.TopLeft+new Point(hitPosition.X,hitPosition.Y);
            bool unknown=_mapLabelUnknown.GetValueOrDefault(obj.RenderIdentity);
            string icon=EnbNavigationMapProjection.Icon(obj.ObjectType,unknown);
            if (_missingGlyphs.Contains(icon)) continue;
            if (!_glyphs.TryGetValue(icon,out var glyph))
            {
                try { _glyphs[icon]=glyph=EnbMapGlyph.Load(_assets,icon); }
                catch (Exception ex) when (ex is InvalidDataException or IOException) { Error=ex.Message; _missingGlyphs.Add(icon); continue; }
            }
            // Marker art is fixed-size in the authored 640x480 interface, not a world-radius circle.
            var mesh=glyph.Resource.Meshes[0];
            float radius=Math.Max(mesh.Positions.Max(p=>Math.Abs(p.X)),mesh.Positions.Max(p=>Math.Abs(p.Y)));
            // 007BEAD0: visited objects larger than the PC glyph use their RenderObj.
            // Render the installed model directly on the GPU instead of retail's black-keyed CPU readback.
            float worldRadius=EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(obj);
            float projectedRadius=worldRadius*_projection.Scale(Viewport);
            var pc=_glyphs.GetValueOrDefault("m_pc.w3d");
            float iconRadius=pc?.Resource.Meshes.Max(m=>m.BoundingRadius) ?? glyph.Resource.Meshes.Max(m=>m.BoundingRadius);
            bool model=!unknown && projectedRadius>iconRadius && obj.ClientBaseAssetReference is not null;
            if (model) radius=projectedRadius;
            var bounds=new Rect(point.X-radius,point.Y-radius,radius*2,radius*2);
            string? name=EnbCurrentRetailRadarCompatibilityAdapter.ResolveContactName(obj);
            if (!bounds.Intersects(Content)) continue;
            string category=EnbCurrentRetailGutterColorRuntime.ResolveCategory(obj.ObjectType,obj.RelationshipRaw,obj.IsAttacking,group);
            // As the radar contact colour (007BB570): an untargeted, unhovered ship with its rescue beacon on
            // uses the authored JumpStart pair (orange) instead of its normal colour.
            string palette=_hoveredObject==obj.GameId ? "Mouse1" : selected==obj.GameId || Guidance.Gate==obj.GameId ? "Target1" :
                obj.ObjectType is 0 or 1 && obj.IsRescueBeaconActive ? "JumpStart1" : "NotTarget1";
            EnbRgb color=new(1,1,1);
            if (_assets.GutterColors?.TryResolve(category,palette,out var resolved)==true) color=resolved;
            float heading=0;
            if (obj.ObjectType==1)
            {
                heading=EnbNext.RetailMath.EnbRetailNavigationReadout.MapHeading(t.OrientationColumn0);
            }
            if(model)
            {
                int slot=models.Count;
                if(slot==_modelStorage.Count)_modelStorage.Add(new());
                var modelObject=_modelStorage[slot];modelObject.AdoptViewObject(obj);
                if(!obj.IsLocalShip)
                {
                    var pose=modelObject.Pose;
                    modelObject.Pose=pose with {Sample=pose.Sample with {Orientation=Quaternion.CreateFromAxisAngle(Vector3.UnitZ,MathF.PI/2),OrientationColumn0=Vector3.UnitY,ProviderTransform=null}};
                }
                models.Add(modelObject);
            }
            else markers.Add(new(glyph,new((float)point.X,(float)point.Y),heading,color));
            if(model)modelPositions[obj.GameId]=new((float)point.X,(float)point.Y);
            _markers[obj.GameId]=(obj,new Rect(hitPoint.X-radius,hitPoint.Y-radius,radius*2,radius*2));
            bool found=_modern && _modernFound==obj.GameId;
            if (!unknown && (_hoveredObject==obj.GameId || selected==obj.GameId || Guidance.Gate==obj.GameId || found || _normalLabelStyles.ContainsKey(obj.RenderIdentity)))
            {
                float opacity=1;
                string style=_hoveredObject==obj.GameId || selected==obj.GameId || Guidance.Gate==obj.GameId || found ? "RADAR_NAME" : _normalLabelStyles[obj.RenderIdentity];
                if(style!="RADAR_NAME" && name=="Unknown")name=_normalLabelInitialNames[obj.RenderIdentity]; // 7C3830
                if(style=="RADAR_JUMPSTART")opacity=Math.Clamp(_rescueLabelAlpha[obj.RenderIdentity],0,1);
                var labelColor=LabelColor(style,(selected==obj.GameId || Guidance.Gate==obj.GameId) && _hoveredObject!=obj.GameId);
                if(Guidance.Gate==obj.GameId) name+=" [Route]";
                AddText("object:"+obj.GameId,name,"FONT_TINY",Logical(new Point(point.X,point.Y-radius)),labelColor,true,true,opacity);
                if (_text.TryGetValue("object:"+obj.GameId,out var label) && label.Raster is not null && !string.IsNullOrEmpty(name))
                {
                    if (_modern) contentImages[^1]=contentImages[^1] with { Bounds=KeepLabelInView(contentImages[^1].Bounds,Logical(new Point(point.X,point.Y+radius)).Y) };
                    _labelBounds[obj.GameId]=(contentImages[^1].Bounds);
                }
            }
        }
        PublishNotes(images,contentImages,AddText);
        _projection.EndRefresh();
        _labelElapsed=0;

        // The Modern UI window has its own title and shows the coordinates in its legend.
        var modernCoordinates=_modern && _scene.ActiveStarbaseId is null && ModernMapPointerCoordinates ? ResolveModernCoordinates() : null;
        if (!_modern) images.AddRange(frameFonts);
        if (!_modern && _scene.ActiveStarbaseId is null && _state.CoordinateMode!=0)
        {
            var coordinates=ResolveCoordinates();
            if (coordinates is { } c)
            {
                string[] values=[c.X,c.Y,c.Z];
                // 007C0C80 gives all three fonts the same viewport top-left.
                // 006D5170/006D64C0 apply their individual authored offsets.
                string[] ids=["STARMAP_X_POS","STARMAP_Y_POS","STARMAP_Z_POS"];
                for(int i=0;i<values.Length;i++)
                {
                    if(_radarLayout?.TryResolve(ids[i],out var definition)!=true)continue;
                    var offset=definition.Properties["Offset"].Split(',')
                        .Select(v=>float.Parse(v,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    var at=Logical(Content.TopLeft+new Point(offset[0]*640,-offset[1]*480));
                    AddText("coord:"+i,values[i],definition.Properties["FontType"],at,c.Color);
                }
            }
        }

        HelpPresentation=null;
        if (_help) ShowTooltip(help:true);
        else ShowGuidance();
        _mapScene.AdoptMetadata(_scene);_mapScene.Objects=models;
        Presentation=new(_frame,_state.Frame,_state.Maximized,(_size),_hovered ?? (_addingMarker ? EnbAuthoredMapFrame.MarkerButtonName : null),images.ToImmutable())
        { Markers=markers.ToImmutable(), ContentImages=contentImages.ToImmutable(), UnderlayImages=SectorShade(), ContentBounds=Content, Help=HelpPresentation,
            Route=ProjectRoute(local,false), WarpRoute=ProjectRoute(local,true), ModelScene=models.Count==0 ? null : _mapScene,
            ModelPositions=modelPositions.ToImmutable(),
            WorldCenter=_projection.Center, WorldScale=mapScale,ModelOwner=(_ownerSession,_owner,_sector) };
        ApplyModern(modernCoordinates);
        foreach (string key in _text.Keys.Where(k=>k.StartsWith("object:",StringComparison.Ordinal) && !usedText.Contains(k)).ToArray())
        { _text[key].Clear(); _text.Remove(key); }
        ShowTooltip(); Changed?.Invoke();
        LastPublishProfile=$"fonts/frame {fontsMs:0.00}ms; total {System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:0.00}ms";

        void AddText(string key,string? value,string fontId,Point at,EnbRgb color,bool center=false,bool above=false,float opacity=1)
        {
            if (string.IsNullOrEmpty(value) || Font(fontId) is not { } font) return;
            usedText.Add(key);
            if (!_text.TryGetValue(key,out var line)) _text[key]=line=new();
            line.SetText(value,font,color); line.SetDisplayScale(1,1);
            if (line.Raster is { } raster) (key.StartsWith("object:",StringComparison.Ordinal) ? contentImages : images).Add(new(raster,new(at.X-(center?line.Width/2:0),at.Y-(above?line.Height:0),line.Width,line.Height)){Opacity=opacity});
        }
    }

    private EnbRetailFontFace? Font(string id)
    {
        if (_assets is null || !EnbRetailFontFactory.TryResolveDefinition(_assets,id,(int)(_size.Width*_scaling),(int)(_size.Height*_scaling),out var definition) || definition.Resource is null) return null;
        if (_fonts.TryGetValue(definition.Resource,out var font)) return font;
        return EnbRetailFontFactory.TryCreate(_assets,definition,out font) ? _fonts[definition.Resource]=font : null;
    }

    private (Size Size,double Scaling) _fontWarmSize;

    private ImmutableArray<Vector2> ProjectRoute(IEnbRenderObject? local,bool executing)
    {
        if (local?.Spatial is null || _scene is null || _scene.ActiveStarbaseId is not null) return [];
        var route = executing ? _scene.Hud.WarpRoute?.ObjectIds ?? [] : _route;
        if (route.IsEmpty) return [];
        float scale=_projection.Scale(Viewport);
        int width=(int)(_size.Width*_scaling),height=(int)(_size.Height*_scaling);
        var camera=EnbRetailScreenPixelGrid.Snap(_projection.ScaledCenter,width,height);
        Vector2 Project(Vector3 p)=>EnbRetailScreenPixelGrid.Snap(new Vector2(p.X,p.Y)*scale,width,height);
        var from=Project(local.Spatial.Transform.Position);
        int first=executing ? (int)(_scene.Hud.WarpRoute?.ReportedIndex ?? 0) : 0;
        uint elapsed=executing ? 0 : _routeElapsed; if(!executing) _routeElapsed=0;
        var points=_routeAnimation.Sample(from,Math.Max(0,route.Length-first),i=>
        {
            uint id=route[first+i];
            return _scene.Objects.FirstOrDefault(o=>o.GameId==id)?.Spatial is {} spatial ? Project(spatial.Transform.Position) : null;
        },elapsed,executing,clip=>InteractionSoundRequested?.Invoke(clip));
        if(points.IsEmpty && !executing) _route=[];
        // 007B9DC0 obtains7BEF20's rendered positions, interpolates those
        // scene coordinates, then the map camera supplies the screen transform.
        return points.Select(p=>Viewport*.5f+new Vector2(p.X-camera.X,camera.Y-p.Y)+
            new Vector2((float)Content.X,(float)Content.Y)).ToImmutableArray();
    }
    private void ClearHover() { _guidanceHovered=false; ClearGalaxyFocus(); _hovered=null; _hoveredObject=null; _dwell.Reset(); TooltipChanged?.Invoke(null); }
    public void SetImmediateTooltips(bool value) { _dwell.Immediate = value; ShowTooltip(_help); }
    private void ShowTooltip(bool help=false)
    {
        if(help) { ShowMapHelp(); return; }
        if(_tooltipFrame is not {} frame || _tooltipDefinition is not {} def || Font(def.FontId) is not {} font)return;
        bool guidance=!_help && !_gesture && _guidanceHovered && _guidanceBounds.Contains(_pointer);
        string message; int identity;
        if(guidance)
        {
            message=Guidance.Destination.HasValue ? Guidance.Status+"\n"+Guidance.RouteText+"\n"+(Guidance.RestrictionNotice.Length>0 ? Guidance.RestrictionNotice+"\n" : "")+"Right-click here to clear route." : "Left-click a sector to set a route.";
            identity=Buttons.Length+1;
        }
        else if(_hovered is {} modernKey && modernKey.StartsWith("mmap/",StringComparison.Ordinal))
        {
            int index=_modernHits.FindLastIndex(h=>h.Id==modernKey);
            if(index<0 || _modernHits[index].Tip.Length==0)return;
            message=_modernHits[index].Tip; identity=Buttons.Length+2+index;
        }
        else
        {
            if(_hovered is not {} key || _strings?.TryGetSection(_galaxy?"UI_GALAXYMAP":"UI_STARMAP",out var strings)!=true)return;
            identity=Array.IndexOf(Buttons,key)+1;
            message=key==EnbAuthoredMapFrame.MarkerButtonName ? MarkerButtonTooltip : strings.GetValueOrDefault(key,"").Replace("\\n","\n");
        }
        int delay=EnbCurrentRetailTooltipDelay.Milliseconds(_assets!.ResolvePlayerOptions(_session()).Options.InterfaceTooltipDelay);
        if(!_dwell.IsReady(identity,Environment.TickCount64,delay))return;
        TooltipChanged?.Invoke(EnbTooltipContent.Create(frame,font,message,_size,_scaling,new(.1f,1,.1f),_pointer,i=>
        {
            string id="tooltip:"+i;
            if(!_text.TryGetValue(id,out var line))_text[id]=line=new();
            return line;
        }));
    }
    private void CenterOnShip(bool normalizeZoom = false)
    {
        if (_scene?.Objects.FirstOrDefault(o=>o.IsLocalShip) is { Spatial: { } spatial } local)
            _projection.CenterOnLocalShip(spatial.Transform.Position,local.GameId,normalizeZoom);
    }
    private EnbRgb LabelColor(string style,bool alternate)
    {
        string color=_radarLayout?.TryResolve(style,out var def)==true ? def.Properties.GetValueOrDefault(alternate ? "AltColor" : "Color", "WHITE") : "WHITE";
        return _labelColors.GetValueOrDefault(color,new(1,1,1));
    }
    private void SetMapCursor(string resource)
    {
        if(CursorResource==resource)return;
        if(CursorRequested?.Invoke(resource)==true)CursorResource=resource;
    }

    private void ClearAssets()
    {
        foreach(var line in _guidanceLines)line.Clear();
        _guidanceLines.Clear(); _guidanceBounds=default; _guidanceKey=null; _guidanceDraw=null;
        Preparation=null;
        _fontWarmSize=default;
        ResetMapFonts();
        ResetGalaxyView(); _galaxyLabelCatalog=null; _galaxyFontCode=null;
        foreach(var line in _text.Values) line.Clear();
        ClearCursorAssetsRequested?.Invoke();
        _labelBounds.Clear(); _radarLayout=null; _helpDefinition=null; _helpFrame=null; HelpPresentation=null;
        _text.Clear(); _fonts.Clear(); _glyphs.Clear(); _missingGlyphs.Clear(); _frame=null; _state=null; _layout=null; _strings=null; _tooltipFrame=null; _tooltipDefinition=null; Error=null;
        _galaxyIni=null; _galaxySystems.Clear(); _galaxySectors.Clear(); _galaxyTextures.Clear();
    }
    public void Dispose() { Suspend(); ClearAssets(); _assets=null; }
}
