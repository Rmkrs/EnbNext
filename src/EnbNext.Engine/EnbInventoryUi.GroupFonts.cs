using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private readonly Dictionary<string,CargoFont> _groupFonts=[];
    private sealed class GroupTextOwner
    {
        public readonly Dictionary<string,EnbAttachedLabelWipe> FontWipes=[];
        public readonly Dictionary<string,string> FontValues=[];
        public readonly EnbGroupFrameClock Clock=new();
    }
    private GroupTextOwner _spaceGroupFonts=new();
    private GroupTextOwner? _stationGroupFonts;
    private GroupTextOwner GroupFontOwner=>_stationGroupFonts??_spaceGroupFonts;
    private void ResetGroupFonts(){_spaceGroupFonts=new();_stationGroupFonts=null;}
    private void AdvanceGroupFontClocks(uint now,bool active)
    {
        if(StationGroup)_stationGroupFonts??=new();
        else _stationGroupFonts=null;
        _spaceGroupFonts.Clock.Advance(now,active&&!StationGroup);
        _stationGroupFonts?.Clock.Advance(now,active);
    }

    private void LoadGroupFonts(EnbRetailAssetBundle assets,EnbAuthoredUiAsset asset)
    {
        _groupFonts.Clear();
        ResetGroupFonts();
        var ini=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fa_tsi03a.ini"));
        foreach(var entry in ini.EnumerateSections())
            if(entry.Value.GetValueOrDefault("StringID") is {} id && _groupLabels!.TryResolve(id,out var definition))
                _groupFonts[id]=new(EnbAttachedTextStyle.Load(assets,asset,definition,Matrix4x4.Identity),
                    definition.Properties.GetValueOrDefault("String0",""),_confirmationColors[definition.Properties["Color"]],
                    _confirmationColors[definition.Properties["AltColor"]],
                    definition.Properties.GetValueOrDefault("EffectOn","").Contains("FA_STAY_COMPLETE",StringComparison.Ordinal));
    }

    // 593BD0 constructs one font collection per main/station group owner.
    // 595530 changes names in mode0 and hides individual fonts; it neither
    // recreates the collection nor resets its effects. HLOD dispatch keeps
    // hidden children ticking (98DD50 -> 6D8270), including empty group slots.
    private void AdvanceGroupFonts()
    {
        foreach(var id in _groupFonts.Keys)GroupFont(id,null,false,false);
    }

    private void GroupFont(string id,string? value,bool alternate,bool draw)
    {
        if(!_groupFonts.TryGetValue(id,out var definition) || Font(definition.Style.Font) is not {} font)return;
        var owner=GroupFontOwner;
        if(value is not null)owner.FontValues[id]=value;
        else if(!owner.FontValues.ContainsKey(id))owner.FontValues[id]=definition.Text;
        string key="group/label/"+id;
        if(!_labels.TryGetValue(key,out var label))_labels[key]=label=new();
        label.SetText(owner.FontValues[id],font,alternate?definition.Alternate:definition.Color);
        label.SetDisplayScale(1,1);
        label.FitTo(Screen(definition.Style.Clip).Size,_scale);
        if(!owner.FontWipes.TryGetValue(id,out var wipe))owner.FontWipes[id]=wipe=new(definition.StayComplete);
        int extent=wipe.Update(owner.Clock.Time,(int)Math.Round(label.Width*_scale),definition.Style.OpeningWipeRightSpeed);
        if(!draw || label.Raster is not {} raster)return;
        var style=definition.Style;var anchor=Screen(new Rect(style.Anchor.X,style.Anchor.Y,0,0));
        var bounds=new Rect(anchor.X-(style.Center?label.Width/2:style.Right?label.Width:0),anchor.Y-label.Height/2,label.Width,label.Height);
        var clip=bounds.Intersect(Screen(style.Clip));
        if(style.OpeningWipeRightSpeed>0)
        {
            clip=clip.Intersect(new(bounds.X,bounds.Y,extent/_scale,bounds.Height));
            _animatingText|=!wipe.Complete;
        }
        if(clip.Width>0 && clip.Height>0)_images.Add(new(raster,(bounds)){Clip=(clip)});
    }
}
