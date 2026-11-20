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
    private readonly Dictionary<string,(EnbAttachedTextStyle Style,float Initial,float Target,float Fade)> _vendorButtonStyles=[];
    private readonly Dictionary<string,EnbAttachedLabelAlpha> _vendorButtonAlpha=[];
    private readonly Dictionary<string,EnbAttachedLabelScroll> _vendorButtonScroll=[];
    private readonly HashSet<string> _vendorLabelsDrawn=[];
    private readonly Dictionary<string,(EnbAttachedTextStyle Style,float Spacing,float Speed,EnbRgb Color)> _vendorNames=[];
    private readonly Dictionary<string,EnbWrappedLabelScroll> _vendorNameMotion=[];

    private void ClearVendorText()
    {
        _vendorButtonStyles.Clear();_vendorButtonAlpha.Clear();_vendorButtonScroll.Clear();_vendorLabelsDrawn.Clear();
        _vendorNames.Clear();_vendorNameMotion.Clear();
    }

    private void LoadVendorButtonText(EnbAuthoredUiAsset asset)
    {
        _vendorButtonStyles.Clear();
        var effects=EnbRetailIniDocument.Load(Path.Combine(_assets!.Paths.ClientIniDirectory,"fontart.ini"));
        _vendorNames.Clear();
        for(int i=0;i<5;i++)
            if(_conversationLayouts["vendor"].TryResolve($"WRAP_text{i:00}_ce02a",out var nameLabel))
            {
                float speed=0;
                foreach(var effect in effects.EnumerateSections().Select(s=>s.Value))
                    if(effect.GetValueOrDefault("EffectID")==nameLabel.Properties.GetValueOrDefault("MouseOver") && effect.GetValueOrDefault("EffectType")=="SCROLL" && effect.GetValueOrDefault("Direction")=="UP")
                        speed=float.Parse(effect["Speed"],System.Globalization.CultureInfo.InvariantCulture);
                _vendorNames[$"NAME{i:00}"]=(EnbAttachedTextStyle.Load(_assets,asset,nameLabel,Matrix4x4.Identity),float.Parse(nameLabel.Properties["Spacing"],System.Globalization.CultureInfo.InvariantCulture),speed,_conversationColors[nameLabel.Properties["Color"]]);
            }
        var names=new[]{"CATEGORY00","CATEGORY01","CATEGORY02","CATEGORY03","CATEGORY04","CATEGORY05","BUY","BUY_STACK","SELL","SELLALL","COMPARE","SORT","CREDITS00","CREDITS_TITLE00"}
            .Concat(Enumerable.Range(0,5).SelectMany(i=>new[]{$"COST{i:00}",$"STACK{i:00}",$"OWNED{i:00}",$"OWNED_TITLE{i:00}"}));
        foreach(string name in names)
        {
            if(!_conversationLayouts["vendor"].TryResolve(name+"_ce02a",out var label))continue;
            var style=EnbAttachedTextStyle.Load(_assets,asset,label,Matrix4x4.Identity);
            float initial=0,target=1,fade=0;
            foreach(var effect in effects.EnumerateSections().Select(s=>s.Value))
                if(effect.GetValueOrDefault("EffectID") is {} id && effect.GetValueOrDefault("EffectType")=="FADE" &&
                   label.Properties.GetValueOrDefault("EffectOn","").Split(',').Any(v=>v.Trim()==id))
                {
                    float Number(string key)=>float.Parse(effect.GetValueOrDefault(key,"0"),System.Globalization.CultureInfo.InvariantCulture);
                    initial=Number("Initial");target=Number("Intensity");fade=Number("Speed");
                }
            _vendorButtonStyles[name]=(style,initial,target,fade);
        }
    }

    private void VendorName(string name,string id,string text)
    {
        if(!_vendorNames.TryGetValue(name,out var definition)||Font(definition.Style.Font) is not {} font)return;
        var style=definition.Style;
        // 006CF930 reserves the normalized width of "..."; 004FAB70 uses
        // individual glyph advances. 006BC(D20/D60) normalize to 640/480.
        double xUnit=font.Scale*640/_size.Width,yUnit=font.Scale*480/_size.Height;
        double width=style.Clip.Width-(font.CharacterAdvance('.')*3-Math.Min(0,font.Spacing))*xUnit;
        var lines=EnbWrappedLabel.Lines(text,width,c=>font.CharacterAdvance(c)*xUnit);
        float height=(float)(font.CellHeight*yUnit),step=height*definition.Spacing;
        float limit=Math.Max(0,Math.Abs(step)*lines.Length-(int)style.Clip.Height);
        if(!_vendorNameMotion.TryGetValue(name,out var motion))_vendorNameMotion[name]=motion=new();
        // 68FE10 -> 6D0A70 replaces rows when the display slot binds another
        // item, including an item with the same display name.
        float offset=motion.Update(text,Environment.TickCount64,_hover==id,limit,definition.Speed,id);
        _vendorLabelsDrawn.Add(name);
        _animatingText|=_hover==id&&motion.Offset<limit||offset!=motion.Offset;
        for(int i=0;i<lines.Length;i++)
        {
            double y=style.Anchor.Y-i*step-offset;
            // 6D64C0 retains the suffix across exact boundary and clipped-out
            // frames. Convert screen-down coordinates to retail's Y-up test.
            bool ellipsis=motion.UpdateEllipsis(i,(float)-y,(float)-style.Clip.Bottom,(int)height,i+1==lines.Length);
            var lineStyle=style with {Anchor=new(style.Anchor.X,y),ScrollSpeed=0};
            AttachedText("vendor"+name+"/"+i,lines[i]+(ellipsis?"...":""),lineStyle,new EnbAttachedLabelScroll(),definition.Color);
        }
    }

    private bool VendorButtonText(string name,string id,string text,EnbRgb color,bool enabled)
    {
        name=name.ToUpperInvariant();
        if(!_vendorButtonStyles.TryGetValue(name,out var definition))return false;
        if(!_vendorButtonAlpha.TryGetValue(name,out var alpha))_vendorButtonAlpha[name]=alpha=new();
        if(!_vendorButtonScroll.TryGetValue(name,out var scroll))_vendorButtonScroll[name]=scroll=new();
        var style=definition.Style;
        bool hovered=enabled&&_hover==id;
        float opacity=alpha.Update(Environment.TickCount64,hovered,definition.Initial,definition.Target,definition.Fade,style.BlinkSpeed,style.RevertSpeed);
        int first=_images.Count;
        AttachedText(id,text,style,scroll,color,fixedLabel:true);
        for(int i=first;i<_images.Count;i++)_images[i]=_images[i] with {Opacity=opacity};
        _vendorLabelsDrawn.Add(name);
        _animatingText|=!alpha.OpeningComplete&&definition.Fade>0 || hovered || opacity<1;
        return true;
    }
}
