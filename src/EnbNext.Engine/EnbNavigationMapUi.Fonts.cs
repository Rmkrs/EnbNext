using Point = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbNavigationMapUi
{
    private static readonly string[] MapFontIds = ["SYSTEM_TSI26A", "DESTINATION_TSI26A",
        "SYSTEM_TITLE_TSI26A", "SYSTEM_NAME_TSI26A", "SECTOR_TITLE_TSI26A", "SECTOR_NAME_TSI26A",
        "STARBASE_TITLE_TSI26A", "STARBASE_NAME_TSI26A"];
    private readonly Dictionary<string,EnbAttachedLabelWipe> _mapFontWipes=[];
    private readonly Dictionary<string,string> _mapFontValues=[];
    private readonly Dictionary<string,EnbAttachedTextStyle> _mapFontStyles=[];
    private float _mapFontFrame=float.NaN;
    private long _mapFontTime;
    private Guid _mapFontSession;
    private bool _mapTextPrepared;
    private ImmutableArray<EnbUiTextDraw> _preparedMapFonts=[];

    private void ResetMapFonts()
    {
        _mapFontWipes.Clear(); _mapFontValues.Clear(); _mapFontStyles.Clear();
        _mapFontTime=0; _mapFontFrame=float.NaN;
        _mapTextPrepared=false;
        _preparedMapFonts=[];
        _mapHelpWipes.Clear(); Array.Clear(_mapHelpTime);
    }

    private void PrepareMapFontStyles()
    {
        if(_layout is null || _assets is null || _frame is null || _state is null)return;
        if(_mapFontFrame==_state.Frame)return;
        _frame.Evaluate(_state.Frame);
        _mapFontStyles.Clear(); _mapFontFrame=_state.Frame;
        foreach(string id in MapFontIds)
            if(_layout.TryResolve(id,out var definition))
                _mapFontStyles[id]=EnbAttachedTextStyle.Load(_assets,_frame.Asset,definition,Matrix4x4.Identity,_frame.Pose.WorldTransforms);
    }

    private ImmutableArray<EnbUiTextDraw> MapFonts(bool prepareOnly=false)
    {
        var images=ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        if(_layout is null || _assets is null || _frame is null || _scene is null || _state is null)return [];
        PrepareMapFontStyles();
        bool station=_scene.ActiveStarbaseId is not null;
        var location=_scene.LocationTransition;
        // 007C8E50 and 007C91A0 replace text in mode 0; they do not restart
        // the retained collection constructed by 007C6BF0.
        if(!_galaxy)
        {
            if(station)
            {
                _mapFontValues["SYSTEM_NAME_TSI26A"]=location?.SourceSystemName??"";
                _mapFontValues["SECTOR_NAME_TSI26A"]=location?.SourceSectorName??"";
                _mapFontValues["STARBASE_NAME_TSI26A"]=location?.DestinationSectorName??"";
            }
            else _mapFontValues["SYSTEM_TSI26A"]=location?.DestinationSectorName??"";
        }
        _mapFontValues["DESTINATION_TSI26A"]=_destinationText;
        foreach(string id in MapFontIds)
        {
            if(!_layout.TryResolve(id,out var definition))continue;
            if(!_mapFontStyles.TryGetValue(id,out var style))
                _mapFontStyles[id]=style=EnbAttachedTextStyle.Load(_assets,_frame.Asset,definition,Matrix4x4.Identity,_frame.Pose.WorldTransforms);
            if(Font(style.Font) is not {} font)continue;
            if(!_mapFontValues.TryGetValue(id,out var value))_mapFontValues[id]=value=definition.Properties.GetValueOrDefault("String0","");
            if(!_text.TryGetValue(id,out var line))_text[id]=line=new();
            line.SetText(value,font,_labelColors[definition.Properties["Color"]]); line.SetDisplayScale(1,1);
            if(prepareOnly)
            {
                // Warm raster upload only; no detached wipe/scroll clock starts.
                if(line.Raster is {} prepared)images.Add(new(prepared,new(0,0,line.Width,line.Height)));
                continue;
            }
            if(!_mapFontWipes.TryGetValue(id,out var wipe))
                _mapFontWipes[id]=wipe=new(definition.Properties.GetValueOrDefault("EffectOn","").Contains("FA_STAY_COMPLETE",StringComparison.Ordinal));
            int extent=wipe.Update(_mapFontTime,(int)Math.Round(line.Width*_scaling),style.OpeningWipeRightSpeed);
            // HLOD 0098DD50 dispatches every attached font. 006D8270 advances
            // its effects before the visibility test: individually hidden labels
            // keep ticking, whereas a detached map has no render updates.
            bool visible=!_galaxy && (station ? id is not ("SYSTEM_TSI26A" or "DESTINATION_TSI26A")
                : id is "SYSTEM_TSI26A" or "DESTINATION_TSI26A");
            if(!visible || line.Raster is not {} raster)continue;
            var anchor=Logical(style.Anchor);
            var bounds=new Rect(anchor.X-(style.Center?line.Width/2:style.Right?line.Width:0),anchor.Y-line.Height/2,line.Width,line.Height);
            var clip=bounds.Intersect(Logical(style.Clip));
            if(style.OpeningWipeRightSpeed>0)clip=clip.Intersect(new(bounds.X,bounds.Y,extent/_scaling,bounds.Height));
            if(clip.Width>0 && clip.Height>0)images.Add(new(raster,(bounds)){Clip=(clip)});
        }
        return images.ToImmutable();
    }
}
