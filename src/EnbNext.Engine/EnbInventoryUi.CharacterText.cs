using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Globalization;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private sealed record CharacterTextDefinition(EnbAttachedTextStyle Style,float Spacing,float Delay,float Wait,float Speed,float Stay,EnbRgb Color,string Vertical,float WipeDown,string InitialText)
    { public (string Name,float Volume,float Duration)? SoundOn; }
    private sealed record CharacterTextRows(object Owner,string Text,Size Size,EnbRetailFontFace Font,double Scale,string[] Lines,EnbWrappedLabelRow[] Rows,EnbWrappedLabelOpening[] Open)
    { public long? Tick; }
    private readonly Dictionary<string,CharacterTextDefinition> _characterTextDefinitions=[];
    private readonly Dictionary<string,CharacterTextRows> _characterTextRows=[];
    private readonly HashSet<string> _characterTextDrawn=[];
    private readonly Dictionary<string,EnbAttachedLabelWipe> _characterOrdinaryWipes=[];
    private readonly HashSet<string> _characterOrdinaryDrawn=[];
    private readonly HashSet<string> _characterOrdinarySeen=[], _characterOrdinarySoundStarted=[];

    private void ResetCharacterOrdinaryText()
    {
        _characterOrdinaryWipes.Clear();
        _characterOrdinarySeen.Clear();
        _characterOrdinarySoundStarted.Clear();
    }

    private void LoadCharacterText(EnbRetailAssetBundle assets,EnbAuthoredUiAsset asset,string prefix,Matrix4x4? parent=null,string suffix="")
    {
        var layout=EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,prefix=="jobs"?"fa_jnews00.ini":prefix is "bio" or "skillFrame" or "skills"?"fa_att.ini":prefix=="skillInfo"?"fa_tsi12c.ini":prefix.StartsWith("faction",StringComparison.Ordinal)?"fa_tsi30a.ini":"fa_tsi22a.ini"));
        var effects=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fontart.ini"));
        var ids=prefix=="jobs"?new[]{"WRAP_DETAIL_JNEWS00"}:prefix=="missions"?Enumerable.Range(0,3).SelectMany(i=>new[]{$"WRAP_Title{i:00}_TSI22A",$"WRAP_DESC{i:00}_TSI22A",$"Time{i:00}_TSI22A",$"{i+1}_TSI22A"})
            :prefix=="missionInfo"?new[]{"WRAP_Title_Detail_TSI22A","WRAP_DESC_DETAIL_TSI22A"}
            :prefix=="factions"?Enumerable.Range(0,6).Select(i=>$"NAME{i:00}_TSI30A").Concat(new[]{"AFFILIATED_TITLE_TSI30A","AFFILIATED_TSI30A","LOVED_TSI30A","HATED_TSI30A"})
            :prefix=="factionInfo"?new[]{"WRAP_NAME_TSI30B","WRAP_DESC_TSI30B"}
            :prefix=="bio"?new[]{"CHARACTER_LEVEL_ATT","CHARACTER_CLASS_NAME_ATT","CHARACTER_RANK_ATT","CHARACTER_CREDITS_ATT","EXP_DEBT_ATT","Combat","Exploration","Trade","Combat_val","Exploration_val","Trade_val"}
            :prefix=="skillFrame"?new[]{"SKILL_POINTS_ATT","SKILL_VAL_ATT"}
            :prefix=="skills"?new[]{"SKILL_NAME_ATT"}
            :new[]{"WRAP_SKILL_DESC_ATT","WRAP_LEVEL_DETAIL_ATT","WRAP_NEXT_LEVEL_DETAIL_ATT","DESC_ATT","LVL_ATT","NOT_ACQUIRED_ATT","NEXT_LVL_ATT","POINTS_AVAILABLE_ATT","UPGRADE_COSTS_ATT","UPGRADE_REQUIRES_ATT","UPGRADE_BUTTON_ATT"};
        foreach(string id in ids)
        {
            if(!layout.TryResolve(id,out var label))continue;
            // 00662FDE attaches this label to DESC00 explicitly, outside the INI.
            if(prefix=="skills")label=label with {Properties=new Dictionary<string,string>(label.Properties){["Attach"]="DESC00"}};
            float Number(string key)=>float.Parse(label.Properties.GetValueOrDefault(key,"0"),CultureInfo.InvariantCulture);
            float speed=0,stay=0,wipeDown=0;EnbRgb color=new(1,1,1);
            foreach(var effect in effects.EnumerateSections().Select(s=>s.Value))
            {
                string? effectId=effect.GetValueOrDefault("EffectID");
                if(effectId is not null && label.Properties.GetValueOrDefault("MouseOver","").Split(',').Any(v=>v.Trim()==effectId)
                    && effect.GetValueOrDefault("EffectType")=="SCROLL" && effect.GetValueOrDefault("Direction")=="UP")
                    speed=float.Parse(effect["Speed"],CultureInfo.InvariantCulture);
                if(effectId is not null && label.Properties.GetValueOrDefault("EffectOn","").Split(',').Any(v=>v.Trim()==effectId)
                    && effect.GetValueOrDefault("EffectType")=="STAY")stay=float.Parse(effect.GetValueOrDefault("Time","0"),CultureInfo.InvariantCulture);
                if(effectId is not null && label.Properties.GetValueOrDefault("EffectOn","").Split(',').Any(v=>v.Trim()==effectId)
                    && effect.GetValueOrDefault("EffectType")=="WIPE" && effect.GetValueOrDefault("Direction")=="DOWN"
                    && float.Parse(effect.GetValueOrDefault("Intensity","0"),CultureInfo.InvariantCulture)>0)
                    wipeDown=float.Parse(effect.GetValueOrDefault("Speed","0"),CultureInfo.InvariantCulture);
                if(effect.GetValueOrDefault("ColorID")==label.Properties.GetValueOrDefault("Color") && effect.GetValueOrDefault("RGB")?.Split(',') is {Length:3} rgb)
                    color=new(byte.Parse(rgb[0])/255f,byte.Parse(rgb[1])/255f,byte.Parse(rgb[2])/255f);
            }
            _characterTextDefinitions[id+suffix]=new(EnbAttachedTextStyle.Load(assets,asset,label,parent??Matrix4x4.Identity),Number("Spacing"),Number("TimeSpacing"),Number("Wait"),speed,stay,color,label.Properties.GetValueOrDefault("Justify","").Split(',')[0].Trim().ToUpperInvariant(),wipeDown,label.Properties.GetValueOrDefault("String0",""));
            if(label.Properties.TryGetValue("SoundOnID",out var cue)&&!string.IsNullOrWhiteSpace(cue))
                _characterTextDefinitions[id+suffix].SoundOn=(cue,
                    float.Parse(label.Properties.GetValueOrDefault("SoundOnVol","1"),CultureInfo.InvariantCulture),
                    float.Parse(label.Properties.GetValueOrDefault("SoundOnDur","-1"),CultureInfo.InvariantCulture));
        }
    }

    // 0054A480 and 0054A640 both call 00548D60, which replaces all three
    // wrapped texts through 006D0A70 even when the text is unchanged.
    private void ClearSkillDetailText()
    {
        _skillTitleScroll.Clear();
        _characterTextRows.Clear();
    }

    // 005C5500/005C5AC0 use SetTextWidgetText(mode=0): replacement does not
    // reset the ordinary label clock (006CDBC0), unlike wrapped-row recreation.
    private void CharacterOrdinaryText(string definitionId,string id,string? text=null)
    {
        if(!_characterTextDefinitions.TryGetValue(definitionId,out var definition)||Font(definition.Style.Font) is not {} font)return;
        _characterOrdinaryDrawn.Add(definitionId);
        if(!_characterOrdinaryWipes.TryGetValue(definitionId,out var wipe))_characterOrdinaryWipes[definitionId]=wipe=new();
        if(!_labels.TryGetValue(id,out var label))_labels[id]=label=new();
        label.SetText(text??definition.InitialText,font,definition.Color);label.SetDisplayScale(1,1);
        var style=definition.Style;
        var clip=Screen(style.Clip);
        double? textTop=null;
        var space=_scale==1?null:CharacterTextSpace(definitionId,style);
        if(space is {} area)
        {
            clip=Screen(area);
            var point=Screen(new Rect(style.Anchor.X,style.Anchor.Y,0,0));
            double height=CharacterLineHeight(definitionId,area)*_size.Height/480;
            textTop=label.FitLineTo(clip,new(point.X,point.Y),height,_scale,style.Center,style.Right);
        }
        else label.FitTo(clip.Size,_scale);
        // 006D5170 skips the opening stage when there are no opening effects.
        // 006D60C0 defers its cue until the second update, while still opening.
        // Mode-0 replacement and hiding preserve this retained font's state.
        if(!_characterOrdinarySeen.Add(definitionId)&&!wipe.Complete&&
            (definition.WipeDown>0||style.OpeningWipeRightSpeed>0)&&
            definition.SoundOn is {} cue&&_characterOrdinarySoundStarted.Add(definitionId))
            CharacterTextSoundRequested?.Invoke(cue.Name,cue.Volume,cue.Duration);
        var anchor=Screen(new Rect(style.Anchor.X,style.Anchor.Y,0,0));
        var bounds=new Rect(anchor.X-(style.Center?label.Width/2:style.Right?label.Width:0),textTop??anchor.Y-label.Height/2,label.Width,label.Height);
        var visible=bounds.Intersect(clip);
        if(definition.WipeDown>0)
        {
            int extent=wipe.Update(Environment.TickCount64,(int)Math.Round(label.Height*_scale),definition.WipeDown);
            visible=visible.Intersect(new Rect(bounds.X,bounds.Y,bounds.Width,extent/_scale));
            _animatingText|=!wipe.Complete;
        }
        else if(style.OpeningWipeRightSpeed>0)
        {
            int extent=wipe.Update(Environment.TickCount64,(int)Math.Round(label.Width*_scale),style.OpeningWipeRightSpeed);
            visible=visible.Intersect(new Rect(bounds.X,bounds.Y,extent/_scale,bounds.Height));
            _animatingText|=!wipe.Complete;
        }
        if(label.Raster is {} raster&&visible.Width>0&&visible.Height>0)_images.Add(new(raster,(bounds)){Clip=(visible)});
    }

    private double CharacterLineHeight(string id,Rect area)
    {
        if(id.StartsWith("SKILL_NAME_ATT",StringComparison.Ordinal))return area.Height;
        double height=area.Height;
        foreach(string other in BiographyLines)
            if(BiographyLineVisible(other) && _characterTextDefinitions.TryGetValue(other,out var definition) && CharacterTextSpace(other,definition.Style) is {} band)
                height=Math.Min(height,band.Height);
        return height;
    }

    private bool BiographyLineVisible(string id)=>id switch
    {
        "CHARACTER_RANK_ATT"=>!string.IsNullOrWhiteSpace(Data?.CharacterStats.GetValueOrDefault("Rank","")),
        "EXP_DEBT_ATT"=>int.TryParse(Data?.CharacterStats.GetValueOrDefault("XPDebt",""),out int debt)&&debt>0,
        _=>true
    };

    private static readonly string[] BiographyLines=["CHARACTER_LEVEL_ATT","CHARACTER_CLASS_NAME_ATT","CHARACTER_RANK_ATT","CHARACTER_CREDITS_ATT","EXP_DEBT_ATT"];
    private Rect? CharacterTextSpace(string id,EnbAttachedTextStyle style)
    {
        if(id.StartsWith("SKILL_NAME_ATT",StringComparison.Ordinal) &&
            int.TryParse(id.AsSpan("SKILL_NAME_ATT".Length),out int bone))
        {
            var anchor=_guildAnchors[$"skillFrame/FILL_BONE{bone:00}"];
            var row=_art["skills/HIGHLIGHT00"].Design.Translate(anchor);
            var dots=_art["notch/off/DOT00_00"].Design.Translate(anchor);
            return new(row.Left,row.Top,row.Width,dots.Top-row.Top);
        }
        if(id is not ("CHARACTER_LEVEL_ATT" or "CHARACTER_CLASS_NAME_ATT" or "CHARACTER_RANK_ATT" or "CHARACTER_CREDITS_ATT" or "EXP_DEBT_ATT"))return null;
        // Shared bands prevent enlarged biography neighbours from claiming the
        // same free space. Empty rank/debt lines do not reserve a band.
        double nameY=_biographyNameStyle?.Anchor.Y??style.Anchor.Y;
        double top=nameY, bottom=Native("bio/BARS _FRONT").Translate(new(-228.5,-58)).Top;
        foreach(string other in BiographyLines)
        {
            if(other==id || !BiographyLineVisible(other))continue;
            double y=_characterTextDefinitions[other].Style.Anchor.Y;
            if(y<style.Anchor.Y)top=Math.Max(top,(y+style.Anchor.Y)/2);
            if(y>style.Anchor.Y)bottom=Math.Min(bottom,(y+style.Anchor.Y)/2);
        }
        if(nameY<style.Anchor.Y)top=Math.Max(top,(nameY+style.Anchor.Y)/2);
        var column=_characterTextDefinitions["CHARACTER_CLASS_NAME_ATT"].Style.Clip;
        return new(column.Left,top,column.Width,bottom-top);
    }

    // 005C5500/005C5DB0 -> 006D0A70 -> 006CF930: independently owned wrapped
    // labels, with attachment hover, delayed row clocks and authored spacing.
    private void CharacterWrappedText(string definitionId,string id,string text,object owner)
    {
        if(!_characterTextDefinitions.TryGetValue(definitionId,out var definition)||Font(definition.Style.Font) is not {} font)return;
        _characterTextDrawn.Add(definitionId);
        var style=definition.Style;
        float xUnit=(float)(font.Scale*640/_size.Width),yUnit=(float)(font.Scale*480/_size.Height);
        float height=font.CellHeight*yUnit,step=height*definition.Spacing;
        if(!_characterTextRows.TryGetValue(definitionId,out var state)||!Equals(state.Owner,owner)||state.Text!=text||state.Size!=_size||!ReferenceEquals(state.Font,font)||state.Scale!=_scale)
        {
            float width=(float)style.Clip.Width-font.MeasureWidth("...")*xUnit;
            var lines=EnbRetailDialogText.Wrap(text.Length==0?" ":text,width,c=>font.CharacterAdvance(c)*xUnit);
            state=new(owner,text,_size,font,_scale,lines,lines.Select((_,i)=>new EnbWrappedLabelRow((int)((definition.Wait+i*definition.Delay)*1000))).ToArray(),
                lines.Select(_=>new EnbWrappedLabelOpening(style.OpeningWipeRightSpeed,definition.Stay)).ToArray());
            _characterTextRows[definitionId]=state;
        }
        long now=Environment.TickCount64;
        int delta=state.Tick is {} tick?(int)Math.Max(0,now-tick):0;state.Tick=now;
        var clip=Screen(style.Clip);
        bool hovered=_point.X>clip.Left&&_point.X<clip.Right&&_point.Y>clip.Top&&_point.Y<clip.Bottom;
        float limit=Math.Max(0,Math.Abs(step)*state.Lines.Length-(int)style.Clip.Height);
        for(int i=0;i<state.Lines.Length;i++)
        {
            var row=state.Rows[i];
            var sample=row.Advance(delta,hovered&&!state.Open[i].Active,limit,definition.Speed);
            if(!sample.Visible){_animatingText=true;continue;}
            double y=style.Anchor.Y-i*step-sample.Offset;
            bool ellipsis=row.UpdateEllipsis((float)-y,(float)-style.Clip.Bottom,(int)height,i+1==state.Lines.Length);
            string key=id+i;
            if(!_labels.TryGetValue(key,out var label))_labels[key]=label=new();
            label.SetText(state.Lines[i]+(ellipsis?"...":""),font,definition.Color);label.SetDisplayScale(1,1);
            var anchor=Screen(new Rect(style.Anchor.X,y,0,0));
            var bounds=new Rect(anchor.X-(style.Center?label.Width/2:style.Right?label.Width:0),anchor.Y-(definition.Vertical is "TOP" or "UP"?0:definition.Vertical is "BOTTOM" or "DOWN"?label.Height:label.Height/2),label.Width,label.Height);
            var visible=bounds.Intersect(clip);
            int extent=state.Open[i].Advance(row.Clock,(int)Math.Round(label.Width*_scale));
            visible=visible.Intersect(new Rect(bounds.X,bounds.Y,extent/_scale,bounds.Height));
            if(label.Raster is {} raster&&visible.Width>0&&visible.Height>0)_images.Add(new(raster,(bounds)){Clip=(visible)});
            _animatingText|=state.Open[i].Active||hovered&&row.Offset<limit||sample.Offset!=row.Offset;
        }
    }
}
