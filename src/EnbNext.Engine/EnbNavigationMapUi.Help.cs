using Point = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using System.Collections.Immutable;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbNavigationMapUi
{
    // Literal row producers in 007C6BF0, addresses 00B9F988..00B9FBF0.
    private static readonly string[][] HelpLines =
    [
        ["Starmap Click Controls:",
         "   -Left click an object icon to select warp destination",
         "   -Right click on map to center location",
         "   -Right click an object icon to lock object to center",
         "Starmap Drag Controls:",
         "   -Left drag on map to pan map around",
         "   -Right drag on map to zoom in and out"],
        ["Galaxy Map Click Controls:",
         "   -Left click a sector for a route; elsewhere to zoom",
         "   -Right click anywhere to zoom out from a location",
         "   -Right click on empty space to zoom full out",
         "Galaxy Map Drag Controls:",
         "   -Left drag on map to pan map around",
         "   -Right drag on map to zoom in and out",
         "   -Left drag with CTRL button to rotate map"]
    ];
    private readonly Dictionary<(int View,int Row),EnbAttachedLabelWipe> _mapHelpWipes=[];
    private readonly long[] _mapHelpTime=new long[2];
    private float _mapHelpWipeSpeed;
    private bool _mapHelpStayComplete;

    private void LoadMapHelpEffects()
    {
        _mapHelpWipeSpeed=0;
        if(_assets is null || _layout?.TryResolve("STARMAP_HOW_TO",out var label)!=true)return;
        string[] effects=label.Properties.GetValueOrDefault("EffectOn","").Split(',').Select(s=>s.Trim()).ToArray();
        var definitions=EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory,"fontart.ini"));
        foreach(var definition in definitions.EnumerateSections().Select(s=>s.Value))
            if(effects.Contains(definition.GetValueOrDefault("EffectID")) && definition.GetValueOrDefault("EffectType")=="WIPE"
                && definition.GetValueOrDefault("Direction")=="RIGHT")
                _mapHelpWipeSpeed=float.Parse(definition["Speed"],CultureInfo.InvariantCulture);
        _mapHelpStayComplete=effects.Contains("FA_STAY_COMPLETE");
    }

    private void ShowMapHelp()
    {
        if(_helpFrame is not {} frame || _helpDefinition is not {} definition || _frame is null
            || Font(definition.FontId) is not {} font || _layout?.TryResolve("STARMAP_HOW_TO",out var label)!=true)return;
        int view=_galaxy?1:0;
        double sx=_size.Width/640,sy=_size.Height/480;
        float fx=(float)(font.Scale/sx),fy=(float)(font.Scale/sy);
        // 0058B160 builds one flags-0x16 row per literal. 0051D160 aligns
        // those rows left; 00516A00/0051F0F0 distribute the two surplus units.
        var layout=EnbRetailDialogLayout.MeasureTextFrame(HelpLines[view].Select(s=>font.MeasureWidth(s)*fx).ToArray(),
            font.CellHeight*fy,new((float)definition.BorderX,(float)definition.BorderY));
        var anchor=_frame.Anchor("QUESTION_BTTN");
        // 007C8645..007C872F: center X at the button, bottom ten units above
        // its translation, then the ordinary 00517C50 screen-edge clamp.
        var placed=EnbRetailDialogLayout.Place(layout.Window.Width,layout.Window.Height,
            (float)anchor.X,(float)anchor.Y-layout.Window.Height*.5f-10);
        var images=ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        for(int i=0;i<HelpLines[view].Length;i++)
        {
            string key=$"help:{view}:{i}";
            if(!_text.TryGetValue(key,out var line))_text[key]=line=new();
            line.SetText(HelpLines[view][i],font,_labelColors[label.Properties["Color"]]);line.SetDisplayScale(1,1);
            if(!_mapHelpWipes.TryGetValue((view,i),out var wipe))_mapHelpWipes[(view,i)]=wipe=new(_mapHelpStayComplete);
            int extent=wipe.Update(_mapHelpTime[view],(int)Math.Round(line.Width*_scaling),_mapHelpWipeSpeed);
            var row=layout.Lines[i];
            var bounds=new Rect(row.X*sx,row.Y*sy+(row.Height*sy-line.Height)/2,line.Width,line.Height);
            if(line.Raster is {} raster)images.Add(new(raster,(bounds))
                {Clip=_mapHelpWipeSpeed>0?new(bounds.X,bounds.Y,extent/_scaling,bounds.Height):null});
        }
        var placement=UiLayout?.Placement(EnbNext.Presentation.EnbUiGroup.Map)??EnbNext.Presentation.EnbUiPlacement.Identity;
        if(!placement.IsIdentity)for(int i=0;i<images.Count;i++){var image=images[i];var local=new EnbNext.Presentation.EnbUiPlacement(0,0,placement.Scale);images[i]=image with {Bounds=local.Apply(image.Bounds,_size),Clip=image.Clip is {} clip?local.Apply(clip,_size):null};}
        HelpPresentation=new(frame,placement.Apply(new Rect(placed.X*sx,placed.Y*sy,placed.Width*sx,placed.Height*sy),_size),
            new(placed.Width,placed.Height)){Lines=images.ToImmutable()};
    }
}
