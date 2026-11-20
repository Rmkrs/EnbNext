using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
using Point = EnbNext.Presentation.EnbUiPoint;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next option: shared map markers. A MARKER_BTTN beside the authored map buttons arms
/// "add marker" mode; the next left click on the sector map opens the marker editor at that
/// point. Left-clicking a marker sets the waypoint arrow to it; right-clicking (or clicking it
/// while add mode is armed) edits it.
/// </summary>
internal sealed partial class EnbNavigationMapUi
{
    private bool _markersEnabled, _addingMarker;
    private Guid? _hoveredNote;
    private readonly Dictionary<Guid,Rect> _noteBounds=[];
    private EnbUiRaster? _markerButtonIcon;
    private EnbRetailAssetBundle? _markerButtonIconOwner;
    private const double NoteRadius=4;
    private const string MarkerButtonTooltip="Add Map Marker\nClick, then click the map to place a marker.\nLeft-click a marker to point the waypoint arrow at it;\nclick it again to clear the arrow.\nRight-click a marker to edit or delete it.";
    private static readonly EnbUiRaster[] NoteIcons=[NoteIcon(new(255,140,26,255)),NoteIcon(new(255,242,77,255)),NoteIcon(new(77,255,77,255))];

    public EnbWaypointBook? Waypoints { get; set; }
    public EnbMapNoteEditor NoteEditor { get; } = new();
    /// <summary>The waypoint arrow option: selecting a marker only matters while it is on.</summary>
    public bool WaypointArrowEnabled { get => NoteEditor.ShowWaypoint; set => NoteEditor.ShowWaypoint=value; }
    public bool MarkersEnabled
    {
        get => _markersEnabled;
        set { if(_markersEnabled==value)return; _markersEnabled=value; if(!value){_addingMarker=false;_hoveredNote=null;NoteEditor.Close();} Publish(); }
    }
    internal bool AddingMarker => _addingMarker;
    private bool NotesActive => _markersEnabled && Waypoints is not null && !_galaxy && _scene?.ActiveStarbaseId is null;
    // Map notes or the temporary /way target are drawn on the sector map.
    private bool MapNotesVisible => (_markersEnabled || WaypointArrowEnabled) && Waypoints is not null && !_galaxy && _scene?.ActiveStarbaseId is null;
    /// <summary>Hit/hover id of the temporary pin: the active waypoint that is not a saved marker.</summary>
    private static readonly Guid TemporaryPin = Guid.Empty;
    private static readonly EnbUiRaster[] PinIcons=[PinIcon(new(102,217,255,255)),PinIcon(new(255,242,77,255))];

    private void WireNoteEditor()
    {
        NoteEditor.Committed=(editor,waypoint)=>
        {
            if(Waypoints is not {} book)return;
            EnbMapNote note;
            if(editor.NoteId is Guid id && book.Notes.FirstOrDefault(n=>n.Id==id) is {} old)
            { book.Update(old with {Name=editor.Name,Note=editor.Note}); note=book.Notes.First(n=>n.Id==id); }
            else note=book.Add(editor.Sector,editor.SectorName,editor.Position,editor.Name,editor.Note);
            // Saving the temporary pin makes it permanent; the arrow keeps pointing at it.
            if(waypoint || editor.FromWaypoint)book.Activate(note);
            Publish();
        };
        NoteEditor.DeleteRequested=id=>{Waypoints?.Remove(id);Publish();};
    }

    private string SectorName(uint sector)
        => GalaxyMapAccessor?.Invoke()?.Sectors.FirstOrDefault(s=>s.SectorHashId==sector)?.Name ?? "This sector";

    private Guid? NoteAt(Point design)
    {
        if(!MapNotesVisible)return null;
        Guid? best=null; double distance=double.MaxValue;
        foreach(var (id,bounds) in _noteBounds)
            if(bounds.Contains(design) && Distance(bounds.Center,design) is var d && d<distance){best=id;distance=d;}
        return best;
    }

    /// <summary>A plain (undragged, no button) click on the sector map. True when markers consumed it.</summary>
    private bool NoteClick(Point point,bool right)
    {
        if(!MapNotesVisible || _sector is not uint sector)return false;
        if(_hoveredNote==TemporaryPin && Waypoints!.Active is {NoteId:null} pin)
        {
            // Right-click (or add mode) opens the marker dialog prefilled; Save makes the pin a marker.
            if(right || _addingMarker)
            {
                if(!_markersEnabled)return false;
                _addingMarker=false;NoteEditor.Open(pin.Sector,pin.SectorName,pin.Position,null,pin.Label,fromWaypoint:true);return true;
            }
            Waypoints.SetActive(null);InteractionSoundRequested?.Invoke("TextClick03.wav");return true;
        }
        if(!NotesActive)return false;
        if(_hoveredNote is Guid id && Waypoints!.Notes.FirstOrDefault(n=>n.Id==id) is {} note)
        {
            if(right || _addingMarker){_addingMarker=false;NoteEditor.Open(note.Sector,note.SectorName,note.Position,note);return true;}
            // Selecting only feeds the waypoint arrow; without it a left click is an ordinary map click.
            if(!WaypointArrowEnabled)return false;
            if(Waypoints.Active?.NoteId==note.Id)Waypoints.SetActive(null); else Waypoints.Activate(note);
            InteractionSoundRequested?.Invoke("TextClick03.wav");
            return true;
        }
        if(!_addingMarker)return false;
        _addingMarker=false;
        if(!right)
        {
            var world=WorldAt(point);
            NoteEditor.Open(sector,SectorName(sector),new(world.X,world.Y,0),null);
        }
        return true;
    }

    private void ToggleAddMarker()
    {
        if(!NotesActive)return;
        _addingMarker=!_addingMarker;
        InteractionSoundRequested?.Invoke("TextClick03.wav");
    }

    private void PublishNotes(ImmutableArray<EnbUiTextDraw>.Builder images,ImmutableArray<EnbUiTextDraw>.Builder contentImages,
        Action<string,string?,string,Point,EnbRgb,bool,bool,float> addText)
    {
        _noteBounds.Clear();
        if(!MapNotesVisible || _sector is not uint sector || _frame is null)return;
        var content=Content; var viewport=Viewport;
        if(WaypointArrowEnabled && Waypoints!.Active is {NoteId:null} pin && pin.Sector==sector)
        {
            var p=_projection.Project(pin.Position,viewport);
            var at=content.TopLeft+new Point(p.X,p.Y);
            var bounds=new Rect(at.X-NoteRadius,at.Y-NoteRadius,NoteRadius*2,NoteRadius*2);
            if(bounds.Intersects(content))
            {
                bool hovered=_hoveredNote==TemporaryPin;
                contentImages.Add(new(PinIcons[hovered?1:0],Logical(bounds)));
                _noteBounds[TemporaryPin]=bounds;
                addText("object:note:pin",pin.Label.Length>0?pin.Label:"Waypoint","FONT_TINY",Logical(new Point(at.X,at.Y-NoteRadius)),
                    hovered?new EnbRgb(1,.95f,.3f):new EnbRgb(.4f,.85f,1),true,true,1);
                if(hovered && _markersEnabled)
                    addText("object:notetext:pin","Right-click to save as marker","FONT_TINY",Logical(new Point(at.X,at.Y+NoteRadius+1)),new(1,1,1),true,false,1);
            }
        }
        if(!NotesActive)return;
        if(_frame.Bounds.TryGetValue(EnbAuthoredMapFrame.MarkerButtonName,out var button) && MarkerButtonIcon() is {} icon)
        {
            // The authored button icons sit inset in their 14.9-unit buttons; the glyph keeps its aspect.
            double w=button.Width*.66, h=w*icon.Height/icon.Width;
            images.Add(new(icon,Logical(new Rect(button.Center.X-w/2,button.Center.Y-h/2,w,h))));
        }
        foreach(var note in Waypoints!.InSector(sector))
        {
            var p=_projection.Project(note.Position,viewport);
            var at=content.TopLeft+new Point(p.X,p.Y);
            var bounds=new Rect(at.X-NoteRadius,at.Y-NoteRadius,NoteRadius*2,NoteRadius*2);
            if(!bounds.Intersects(content))continue;
            bool hovered=_hoveredNote==note.Id, active=WaypointArrowEnabled && Waypoints.Active?.NoteId==note.Id;
            contentImages.Add(new(NoteIcons[hovered?1:active?2:0],Logical(bounds)));
            _noteBounds[note.Id]=bounds;
            var color=hovered?new EnbRgb(1,.95f,.3f):active?new EnbRgb(.3f,1,.3f):new EnbRgb(1,.55f,.1f);
            addText("object:note:"+note.Id,note.Name,"FONT_TINY",Logical(new Point(at.X,at.Y-NoteRadius)),color,true,true,1);
            if(hovered && note.Note.Length>0)
                addText("object:notetext:"+note.Id,note.Note,"FONT_TINY",Logical(new Point(at.X,at.Y+NoteRadius+1)),new(1,1,1),true,false,1);
        }
        if(_addingMarker)
            addText("marker-hint","Click the map to place a marker. Right-click cancels.","FONT_TINY",
                Logical(content.TopLeft+new Point(content.Width/2,4)),new(1,.95f,.3f),true,false,1);
    }

    private EnbUiRaster? MarkerButtonIcon()
    {
        if(ReferenceEquals(_markerButtonIconOwner,_assets))return _markerButtonIcon;
        _markerButtonIconOwner=_assets; _markerButtonIcon=null;
        // The pointing hand from the map's own icon atlas (IF_MAIN01, the same sheet as ? / center / xyz).
        const int x0=102,y0=119,x1=122,y1=131;
        if(_assets is null || !_assets.Textures.TryReadCurrentRetailSource("if_main01.tga",false,out var bytes,out _) ||
            !EnbRetailTextureDecoder.TryDecode(bytes,"if_main01.tga",out var source) || source.Width<x1 || source.Height<y1)return null;
        var pixels=source.Pixels.Span; var crop=new EnbColor32[(x1-x0)*(y1-y0)];
        for(int y=y0;y<y1;y++)for(int x=x0;x<x1;x++)crop[(y-y0)*(x1-x0)+x-x0]=pixels[y*source.Width+x];
        return _markerButtonIcon=EnbUiRaster.FromPixels(x1-x0,y1-y0,crop);
    }

    /// <summary>The temporary /way pin: a ring with a centre dot, unlike the marker diamonds.</summary>
    private static EnbUiRaster PinIcon(EnbColor32 fill)
    {
        const int size=16, samples=4; var pixels=new EnbColor32[size*size];
        for(int y=0;y<size;y++)for(int x=0;x<size;x++)
        {
            float inner=0,outer=0;
            for(int sy=0;sy<samples;sy++)for(int sx=0;sx<samples;sx++)
            {
                float px=(x+(sx+.5f)/samples)-size/2f, py=(y+(sy+.5f)/samples)-size/2f;
                float r=MathF.Sqrt(px*px+py*py);
                if(r<=2.2f || r>=4.6f && r<=6.4f)inner++; else if(r<=7.6f || r>2.2f && r<3.4f)outer++;
            }
            inner/=samples*samples; outer/=samples*samples;
            float a=inner+outer*.55f; if(a<=0)continue;
            float k=inner/(inner+outer);
            pixels[y*size+x]=new((byte)(fill.R*k),(byte)(fill.G*k),(byte)(fill.B*k),(byte)(Math.Min(1,a)*255));
        }
        return EnbUiRaster.FromPixels(size,size,pixels);
    }

    /// <summary>A small diamond with a dark rim: unlike every authored nav, gate or station glyph.</summary>
    private static EnbUiRaster NoteIcon(EnbColor32 fill)
    {
        const int size=16, samples=4; var pixels=new EnbColor32[size*size];
        for(int y=0;y<size;y++)for(int x=0;x<size;x++)
        {
            float inner=0,outer=0;
            for(int sy=0;sy<samples;sy++)for(int sx=0;sx<samples;sx++)
            {
                float px=(x+(sx+.5f)/samples)-size/2f, py=(y+(sy+.5f)/samples)-size/2f;
                float d=MathF.Abs(px)+MathF.Abs(py);
                if(d<=5.6f)inner++; else if(d<=7.6f)outer++;
            }
            inner/=samples*samples; outer/=samples*samples;
            float a=inner+outer; if(a<=0)continue;
            float k=inner/a, centre=MathF.Max(0,1-(MathF.Abs(x+.5f-size/2f)+MathF.Abs(y+.5f-size/2f))/2.2f)*k;
            byte C(byte c)=>(byte)Math.Clamp(c*k+(255-c)*centre*.6f,0,255);
            pixels[y*size+x]=new(C(fill.R),C(fill.G),C(fill.B),(byte)(255*a));
        }
        return EnbUiRaster.FromPixels(size,size,pixels);
    }
}
