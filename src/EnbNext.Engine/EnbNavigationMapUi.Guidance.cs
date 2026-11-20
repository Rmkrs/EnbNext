using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

internal sealed partial class EnbNavigationMapUi
{
    internal EnbSectorRouteGuidance Guidance { get; init; } = new();
    /// <summary>Next Modern UI galaxy map window: it shows the sector map's route guidance, which the sector map keeps up to date.</summary>
    internal bool SharesGuidance { get; init; }
    internal bool RoutePlannerEnabled { get; private set; }
    private Rect _guidanceBounds;
    private bool _guidancePressed, _guidanceHovered;
    private readonly List<EnbRetailText> _guidanceLines = [];
    private (string Status,string Route,bool Galaxy,Rect Content,EnbUiSize Size,EnbUiPlacement Placement,EnbRetailFontFace Font)? _guidanceKey;
    private EnbUiTooltipDraw? _guidanceDraw;

    internal static string CompactRoute(string route,Func<string,double> measure,double width)
    {
        if(measure(route)<=width)return route;
        var sectors=route.Split(" > ",StringSplitOptions.None);
        for(int count=sectors.Length-2;count>=1;count--)
        {
            string candidate=string.Join(" > ",sectors.Take(count))+" > ... > "+sectors[^1];
            if(measure(candidate)<=width)return candidate;
        }
        return FitGuidanceLine(sectors.Length>1 ? "... > "+sectors[^1] : route,measure,width);
    }

    private static string FitGuidanceLine(string text,Func<string,double> measure,double width)
    {
        if(measure(text)<=width)return text;
        while(text.Length>0 && measure(text+"...")>width)text=text[..^1];
        return measure(text+"...")<=width ? text+"..." : "";
    }

    private void ShowGuidance()
    {
        _guidanceBounds=default;
        // The Modern UI window shows the route in its side panel.
        if (_modern) return;
        if (!RoutePlannerEnabled || (_scene?.ActiveStarbaseId is not null && !_galaxy) || (Guidance.Destination.HasValue && Guidance.Status.Length==0) ||
            (!_galaxy && !Guidance.Destination.HasValue) || _frame is null ||
            _helpFrame is not {} frame || _helpDefinition is not {} definition || Font(definition.FontId) is not {} font) return;
        double sx=_size.Width/640, sy=_size.Height/480;
        var content=Logical(Content);
        var placement=UiLayout?.Placement(EnbUiGroup.Map)??EnbUiPlacement.Identity;
        var key=(Guidance.Status,Guidance.RouteText,_galaxy,content,_size,placement,font);
        if(_guidanceKey==key && _guidanceDraw is {} retained)
        { HelpPresentation=retained; _guidanceBounds=retained.Bounds; return; }
        double borderX=definition.BorderX*sx, borderY=definition.BorderY*sy;
        double width=Math.Max(1,content.Width-2*borderX);
        double Measure(string text)=>font.MeasureWidth(text)*font.Scale;
        string[] display=[FitGuidanceLine(Guidance.Destination.HasValue ? Guidance.Status : "Left-click a sector to set a route.",Measure,width),
            CompactRoute(Guidance.RouteText,Measure,width)];
        var color=_layout?.TryResolve("STARMAP_HOW_TO",out var style)==true &&
            _labelColors.TryGetValue(style.Properties["Color"],out var authored) ? authored : new EnbRgb(1,1,1);
        var images=ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        double rowHeight=font.CellHeight*font.Scale;
        for(int i=0;i<2;i++)
        {
            if(i==_guidanceLines.Count)_guidanceLines.Add(new());
            var line=_guidanceLines[i];line.SetText(display[i],font,color);line.SetDisplayScale(1,1);
            if(line.Raster is {} raster) images.Add(new(raster,new Rect(borderX,borderY+i*rowHeight,line.Width,line.Height)));
        }
        double height=2*rowHeight+2*borderY;
        var bounds=new Rect(content.X,content.Bottom-height,content.Width,height);
        _guidanceBounds=placement.Apply(bounds,_size);
        if(!placement.IsIdentity)
            for(int i=0;i<images.Count;i++)images[i]=images[i] with {Bounds=new EnbUiPlacement(0,0,placement.Scale).Apply(images[i].Bounds,_size)};
        // Use one artwork scale on both axes so the authored transparent rim
        // has the same physical inset on the sides and bottom. Text stays unscaled.
        HelpPresentation=new(frame,_guidanceBounds,new(bounds.Width/sx,bounds.Height/sx)){Lines=images.ToImmutable(),FitFrameToBounds=true};
        _guidanceDraw=HelpPresentation; _guidanceKey=key;
    }
}
