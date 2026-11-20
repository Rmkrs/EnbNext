using Point = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Net7.Protocol;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbNavigationMapUi
{
    private bool _galaxy, _galaxyGrid;
    // 007AB8B0 initializes sectors; 007A8B70 admits only planet subtypes
    // to 007ABC60's update list. The home object is updated by 007A7070.
    private const float GalaxyInitialHeading = (float)0.2617993950843811;
    private const float GalaxyHeadingStep = 0.01f;
    private Matrix4x4 _galaxyPlanetBasis = EnbGalaxyMapModelRotation.InitialSector(), _galaxyHomeBasis = Matrix4x4.Identity;
    private float _galaxyPlanetHeading = GalaxyInitialHeading, _galaxyHomeHeading;

    private void AdvanceGalaxyModels()
    {
        var previous = _positionedGalaxy;
        LoadGalaxy();
        if (!ReferenceEquals(previous, _positionedGalaxy)) return;
        _galaxyPlanetBasis = EnbGalaxyMapModelRotation.Advance(_galaxyPlanetBasis);
        _galaxyHomeBasis = EnbGalaxyMapModelRotation.Advance(_galaxyHomeBasis);
        _galaxyPlanetHeading += GalaxyHeadingStep;
        _galaxyHomeHeading += GalaxyHeadingStep;
    }
    private EnbNavigationMapProjection _sectorProjection=new(), _galaxyProjection=new();
    private EnbRetailIniDocument? _galaxyIni;
    private readonly Dictionary<uint,Vector3> _galaxySystems=[];
    private readonly Dictionary<uint,Vector3> _galaxySectors=[];
    private readonly Dictionary<(string,EnbRgb),EnbUiRaster> _galaxyTextures=[];
    private readonly Dictionary<uint,(uint Tick,Vector2 Offset)> _galaxyLineScroll=[];
    private static readonly Net7GalaxyMapData EmptyGalaxy = new([], [], [], []);
    private Net7GalaxyMapData Galaxy = EmptyGalaxy;
    private Net7GalaxyMapData? _positionedGalaxy;
    private uint _galaxyNextRefreshTick;
    public Func<Net7GalaxyMapData?>? GalaxyMapAccessor { get; set; }
    public Func<Task<bool>>? ToggleGalaxyMapRequested { get; set; }

    private void ResetGalaxyView()
    {
        _positionedGalaxy=null; _galaxyNextRefreshTick=0; _galaxyCameraInitialized=false; _galaxyCamera=new();
        _galaxySystemOrder=_galaxySectorOrder=_galaxyGateOrder=Net7GalaxyMapOrder.Empty;
        _galaxySectorConstruction=[]; _galaxyGateConstruction=[];
        _galaxyLabels.Clear(); _galaxyLabelFocus=null; _galaxyLineFade.Clear(); _galaxyLineScroll.Clear();
        _galaxyRenderElapsed=0; _galaxyHomeHeading=0; _galaxyHomeBasis=Matrix4x4.Identity; ClearGalaxyFocus();
    }

    private async Task RequestGalaxyMapToggleAsync()
    {
        try { if (ToggleGalaxyMapRequested is { } request) await request(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        { /* A retired gameplay route cannot replay this click into another session. */ }
    }


    public bool ToggleMode(bool galaxy)
    {
        if (!_interactive || _frame is null) return false;
        if (IsOpen && _galaxy==galaxy) return Close();
        long opening = galaxy && TimingGalaxy ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (!IsOpen && !Toggle()) return false;
        if (_galaxy!=galaxy) SwitchMapMode();
        Publish();
        if (opening != 0) _galaxyTiming += " · galaxy open " + Since(opening).ToString("0.0", CultureInfo.InvariantCulture) + " ms";
        return true;
    }

    private float GalaxySetting(string key)
    {
        if (_galaxyIni?.TryGetSection("GalaxyMap",out var section)!=true ||
            !section.TryGetValue(key,out var value) || !float.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out float number))
            throw new InvalidDataException("Missing galaxy_map_c.ini GalaxyMap."+key);
        return number;
    }

    private void LoadGalaxy()
    {
        var latest = GalaxyMapAccessor?.Invoke() ?? EmptyGalaxy;
        if (_galaxyIni is not null && ReferenceEquals(latest, _positionedGalaxy)) return;
        // 007A7070 coalesces revisions, retaining the displayed snapshot while
        // its deadline has not passed. Animation still runs on those updates.
        uint now=unchecked((uint)Environment.TickCount64);
        if (_positionedGalaxy is not null && _galaxyNextRefreshTick>=now) return;
        Galaxy=latest;
        _galaxyIni ??= EnbRetailIniDocument.Load(Path.Combine(_assets!.Paths.ClientIniDirectory,"galaxy_map_c.ini"));
        _galaxySystems.Clear(); _galaxySectors.Clear();
        float systemScale=GalaxySetting("SystemPosScale"),sectorScale=GalaxySetting("SectorPosScale");
        var shift=new Vector3(GalaxySetting("GalaxyShiftX"),GalaxySetting("GalaxyShiftY"),0);
        // client.exe 007AAB60 / 007A5470: VectorA is color, VectorB*Scalar is position.
        foreach(var system in Galaxy.Systems)
            _galaxySystems[system.SystemHashId]=new Vector3(system.VectorB[0],system.VectorB[1],system.VectorB[2])*system.Scalar*new Vector3(systemScale,systemScale,1)+shift;
        // 007AB6D0 / 007AB8B0 / 007A54F0: Field2 is the parent system hash.
        foreach(var sector in EnbGalaxyMapPresentation.VisibleSectors(Galaxy))
            _galaxySectors[sector.SectorHashId]=_galaxySystems[sector.Field2]+new Vector3(sector.Vector[0]*sectorScale,sector.Vector[1]*sectorScale,sector.Vector[2]);
        _galaxyPlanetHeading = GalaxyInitialHeading;
        _galaxyPlanetBasis = EnbGalaxyMapModelRotation.InitialSector();
        _galaxyLineFade.Clear(); _galaxyLabelFocus=null;
        // 007A8B70 retains surviving owners; 007A9500 reinitializes line
        // style/fade without reconstructing its clock or retained UV offset.
        var retainedGates=Galaxy.Gates.Where(g=>_positionedGalaxy?.Gates.Any(old=>ReferenceEquals(old,g))==true)
            .Select(g=>g.GateHashId).ToHashSet();
        foreach(uint key in _galaxyLineScroll.Keys.Where(k=>!retainedGates.Contains(k)).ToArray()) _galaxyLineScroll.Remove(key);
        var retainedLabels=Galaxy.Systems.Where(s=>_positionedGalaxy?.Systems.Any(old=>ReferenceEquals(old,s))==true)
            .Select(s=>"galaxy:system:"+s.SystemHashId).Concat(EnbGalaxyMapPresentation.VisibleSectors(Galaxy)
                .Where(s=>_positionedGalaxy?.Sectors.Any(old=>ReferenceEquals(old,s))==true).Select(s=>"galaxy:sector:"+s.SectorHashId)).ToHashSet();
        foreach(string key in _galaxyLabels.Keys.Where(k=>!retainedLabels.Contains(k)).ToArray()) _galaxyLabels.Remove(key);
        uint created=unchecked((uint)Environment.TickCount64);
        foreach(var gate in Galaxy.Gates) _galaxyLineScroll.TryAdd(gate.GateHashId,(created,Vector2.Zero));
        _positionedGalaxy = Galaxy;
        _galaxyNextRefreshTick=unchecked((uint)Environment.TickCount64+1000);
        RefreshGalaxyOrder();
        BuildGalaxyBounds();
    }

    private void FitGalaxy()
    {
        FrameGalaxyHome(false);
    }

    private void SwitchMapMode()
    {
        CancelPointer(); ClearHover(); _help=false; HelpPresentation=null;
        if (_galaxy) { ClearGalaxyFocus(); _galaxyProjection=_projection; _projection=_sectorProjection; _galaxy=false; }
        else
        {
            try { AdvanceGalaxyModels(); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            { _galaxyIni=null; _galaxySystems.Clear(); _galaxySectors.Clear(); Error=ex.Message; return; }
            _sectorProjection=_projection; _projection=_galaxyProjection; _galaxy=true;
            FrameGalaxyHome(true);
        }
        _markers.Clear(); _labelBounds.Clear();
    }

    private void PublishGalaxy()
    {
        long loadingData = TimingGalaxy ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        LoadGalaxy();
        if (loadingData != 0) _galaxyLoadMs += Since(loadingData);
        if (Schematic) { PublishSchematic(); return; }
        if (!_galaxyCameraInitialized) FrameGalaxyHome(true);
        LocateGalaxy();
        var sectors = EnbGalaxyMapPresentation.VisibleSectors(Galaxy);
        var sectorById = sectors.ToDictionary(s => s.SectorHashId);
        var markers=ImmutableArray.CreateBuilder<EnbMapMarker>();
        var images=ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        var labels=ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        var backgrounds=ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        var connections=ImmutableArray.CreateBuilder<EnbMapConnection>();
        var worldBackgrounds=ImmutableArray.CreateBuilder<EnbMapQuad>();
        var worldLinks=ImmutableArray.CreateBuilder<EnbMapQuad>();
        var clip=Content;
        _markers.Clear(); _labelBounds.Clear();

        Vector2 Project(Vector3 position)=>ProjectGalaxy(position);
        EnbMapGlyph Glyph(string name)
        {
            if (_glyphs.TryGetValue(name,out var glyph)) return glyph;
            long loading = TimingGalaxy ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            _glyphs[name]=glyph=EnbMapGlyph.Load(_assets!,name,name=="sm_grid.w3d" ? GalaxySetting("Size") : 1);
            if (loading != 0) _galaxyGlyphMs += Since(loading);
            return glyph;
        }
        void Marker(string name,Vector3 at,Vector2 size,EnbRgb color, bool preserveMaterial = false, float heading = 0, Matrix4x4? basis = null)
            =>markers.Add(new(Glyph(name),Project(at),heading,color) { Scale=size, PreserveMaterial=preserveMaterial,
                WorldTransform=Matrix4x4.CreateScale(size.X,size.Y,size.X)*(basis ?? Matrix4x4.CreateRotationZ(heading))*Matrix4x4.CreateTranslation(at) });
        Marker("sm_galaxy.w3d",Vector3.Zero,new(GalaxySetting("Size")),new(1,1,1),true);
        if (_galaxyGrid) Marker("sm_grid.w3d",Vector3.Zero,new(GalaxySetting("Size")*2),new(1,1,1),true);
        int backgroundCount=markers.Count;
        foreach(var system in Galaxy.Systems)
        {
            var (min,max)=_galaxySystemBounds[system.SystemHashId];
            var tl=Project(new(min.X,max.Y,0)); var br=Project(new(max.X,min.Y,0));
            var color=new EnbRgb(system.VectorA[0],system.VectorA[1],system.VectorA[2]);
            var raster=Texture("sm_system00.tga",color);
            // 007B3440's raw ESI loads retain the first child's orientation
            // selector; 007B3770 flips each axis, then optionally transposes UV.
            var first=_galaxySectorConstruction.Select(key=>sectorById[key]).FirstOrDefault(s=>s.Field2==system.SystemHashId);
            var relative=first is null ? Vector3.Zero : _galaxySectors[first.SectorHashId]-_galaxySystems[system.SystemHashId];
            float u=relative.X>0 ? 1 : 0;
            float v=relative.Y<0 ? 1 : 0;
            Vector2 Uv(float x,float y)=>(system.Field0&1)!=0 ? new(y,x) : new(x,y);
            uint phase=_galaxySystemPulse%1000;
            if(phase>500) phase=1000-phase;
            float brightness=_galaxyFocusedSystem==system.SystemHashId ? 1-phase*.002f*.75f : 1;
            worldBackgrounds.Add(new(raster,new(min.X,max.Y,min.Z),max,min,new(max.X,min.Y,min.Z),
                Uv(u,v),Uv(1-u,v),Uv(u,1-v),Uv(1-u,1-v),brightness));
        }
        for(int gateIndex=0;gateIndex<Galaxy.Gates.Count;gateIndex++)
        {
            var gate=Galaxy.Gates[gateIndex];
            // 007ACBB0: a link needs both admitted endpoint presentations.
            if (!sectorById.TryGetValue(gate.SectorReferenceA, out var from) ||
                !sectorById.TryGetValue(gate.SectorReferenceB, out var to)) continue;
            float opacity=GalaxyLinkOpacity(gateIndex);
            if (opacity<=0) continue;
            bool intra=from.Field2==to.Field2;
            if (!_galaxyIni!.TryGetSection(intra ? "IntraGateLine" : "InterGateLine",out var style)) throw new InvalidDataException("Galaxy gate line style missing.");
            float Number(string key)=>float.Parse(style[key],CultureInfo.InvariantCulture);
            var color=EnbGalaxyMapPresentation.LinkColor(Galaxy,from,to);
            var a=_galaxySectors[gate.SectorReferenceA]; var b=_galaxySectors[gate.SectorReferenceB];
            // 007B3F00 partitions a link until each segment's UV tile <=256.
            float repeat=Vector3.Distance(a,b)*Math.Clamp(Number("UVTile"),0,256);
            int segments=1;
            while(repeat>256) { repeat*=.5f; segments*=2; }
            var points=new Vector3[segments+1];
            var step=(b-a)/segments; var position=a;
            for(int i=0;i<segments;i++) { points[i]=position; position+=step; }
            points[segments]=b;
            var rates=style["UVRate"].Split(',').Select(s=>float.Parse(s,CultureInfo.InvariantCulture)).ToArray();
            // 009BE7E0 owns the initial clock; 009BEF17..009BEF93 retains
            // fractional UV offsets and advances only when the line renders.
            var previous=_galaxyLineScroll[gate.GateHashId];
            uint tick=unchecked((uint)Environment.TickCount64), elapsed=unchecked(tick-previous.Tick);
            float Scroll(float rate,float offset) { float next=offset+elapsed*(rate*.001f); return (float)(next-Math.Floor(next)); }
            var scroll=new Vector2(Scroll(rates[0],previous.Offset.X),Scroll(rates[1],previous.Offset.Y));
            _galaxyLineScroll[gate.GateHashId]=(tick,scroll);
            var geometry=EnbRetailProceduralLineGeometry.Build(points,GalaxyRenderCamera(),0,0,false,Number("Width")*.5f,
                (int)Number("UVMode"),repeat,scroll);
            var texture=Texture(style["Texture"],color);
            for(int i=0;i<geometry.Indices.Length;i+=3)
            {
                int x=(int)geometry.Indices[i],y=(int)geometry.Indices[i+1],z=(int)geometry.Indices[i+2];
                worldLinks.Add(new(texture,geometry.Positions[x],geometry.Positions[y],geometry.Positions[z],geometry.Positions[z],
                    geometry.Uv[x],geometry.Uv[y],geometry.Uv[z],geometry.Uv[z],opacity));
            }
        }
        uint? hovered=_galaxyFocusedSector;
        var nearest=sectors.FirstOrDefault(s=>s.SectorHashId==hovered);
        foreach(var sector in sectors)
        {
            var color=EnbGalaxyMapPresentation.SectorColor(Galaxy,sector,GalaxySectorHighlighted(sector.SectorHashId,sector.Field2));
            var position=_galaxySectors[sector.SectorHashId]; var point=Project(position);
            if (GalaxyMainVisible(sector.SectorHashId)) Marker(sector.Subtype is 7 or 8 ? "sm_planet.w3d" : "sm_sector.w3d",position,new(sector.Subtype is 7 or 8 ? 1.5f : 1),color, heading: sector.Subtype is 7 or 8 ? _galaxyPlanetHeading : GalaxyInitialHeading, basis: sector.Subtype is 7 or 8 ? _galaxyPlanetBasis : EnbGalaxyMapModelRotation.InitialSector());
        }
        // 007AAF20 scales sm_dot by .25; 007ABB80 attaches it independently.
        // 007AB8B0 translates it without applying the planet's rotation/color.
        foreach(var sector in sectors)
            Marker("sm_dot.w3d",_galaxySectors[sector.SectorHashId],new(.25f),new(1,1,1),true);
        // 005404E0 selects AuxData SectorNum; 007A8770 uses its inverted color.
        if (_scene?.Environment?.SectorNum is { } current && sectorById.TryGetValue(current, out var home))
        {
            try { Marker("sm_sector_home.w3d",_galaxySectors[current],Vector2.One,EnbGalaxyMapPresentation.SectorColor(Galaxy,home,true), heading: _galaxyHomeHeading, basis: _galaxyHomeBasis); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            { Error=ex.Message; } // 007A8770 tolerates an unavailable home render object.
        }
        HelpPresentation=null;
        if (_help) ShowTooltip(help:true);
        else ShowGuidance();
        long labelling = TimingGalaxy ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        ResolveGalaxyLabels(sectors);
        var worldLabels=DrawGalaxyLabels();
        if (labelling != 0) _galaxyLabelMs += Since(labelling);
        Presentation=new(_frame!,_state!.Frame,_state.Maximized,(_size),_hovered,images.ToImmutable())
        {
            Markers=markers.ToImmutable(), ContentImages=labels.ToImmutable(), ContentBounds=(clip),
            BackgroundImages=backgrounds.ToImmutable(), Connections=connections.ToImmutable(), BackgroundMarkerCount=backgroundCount, Help=HelpPresentation
            , GalaxyCamera=GalaxyRenderCamera(), GalaxyBackgrounds=worldBackgrounds.ToImmutable(), GalaxyLinks=worldLinks.ToImmutable(), GalaxyLabels=worldLabels
        };
        ApplyModern(null);
        _galaxyRenderElapsed=0;
        ShowTooltip(); Changed?.Invoke();

    }

    private EnbUiRaster Texture(string name,EnbRgb color)
    {
        if(_galaxyTextures.TryGetValue((name,color),out var raster)) return raster;
        long decoding = TimingGalaxy ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try {
        var image=_assets!.Textures.Resolve(name).Image ?? throw new InvalidDataException("Missing "+name);
        var pixels=new byte[image.Width*image.Height*4];
        for(int i=0;i<image.Pixels.Length;i++)
        {
            var p=image.Pixels.Span[i];
            pixels[i*4]=(byte)(p.B*color.B); pixels[i*4+1]=(byte)(p.G*color.G); pixels[i*4+2]=(byte)(p.R*color.R); pixels[i*4+3]=p.A;
        }
        return _galaxyTextures[(name,color)]=new(image.Width,image.Height,ImmutableArray.Create(pixels));
        } finally { if (decoding != 0) _galaxyTextureMs += Since(decoding); }
    }
}
