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
    private readonly Dictionary<string,(EnbAttachedTextStyle Style,float Initial,float Target,float Fade)> _terminalOrdinaryStyles=[];
    private readonly Dictionary<string,EnbAttachedLabelAlpha> _terminalOrdinaryAlpha=[];
    private readonly Dictionary<string,EnbAttachedLabelScroll> _terminalOrdinaryScroll=[];
    private readonly Dictionary<string,EnbAttachedLabelWipe> _terminalOrdinaryWipes=[];
    private readonly HashSet<string> _terminalOrdinaryDrawn=[];

    private void LoadTerminalOrdinaryText(string model,EnbAuthoredUiAsset asset,IReadOnlyList<Matrix4x4> pose)
    {
        if(!_terminalLabels.TryGetValue(model,out var labels))return;
        var effects=EnbRetailIniDocument.Load(Path.Combine(_assets!.Paths.ClientIniDirectory,"fontart.ini"));
        foreach(var label in labels)
        {
            string name=label.Properties.GetValueOrDefault("Attach","").ToUpperInvariant();
            if(name.StartsWith("DESC",StringComparison.Ordinal))continue;
            if(model=="jnews00" && (IsJobRowLabel(name)||name=="DETAIL"))continue;
            //Keep the existing admission boundary: obsolete INI entries without
            //a model attachment never reached TerminalLabel's anchor consumer.
            if(!asset.Hierarchy.Pivots.Any(p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))continue;
            var style=EnbAttachedTextStyle.Load(_assets,asset,label,Matrix4x4.Identity,pose);
            float initial=0,target=1,fade=0;
            foreach(var effect in effects.EnumerateSections().Select(s=>s.Value))
                if(effect.GetValueOrDefault("EffectID") is {} id&&effect.GetValueOrDefault("EffectType")=="FADE"&&
                    label.Properties.GetValueOrDefault("EffectOn","").Split(',').Any(v=>v.Trim()==id))
                {
                    float Number(string key)=>float.Parse(effect.GetValueOrDefault(key,"0"),CultureInfo.InvariantCulture);
                    initial=Number("Initial");target=Number("Intensity");fade=Number("Speed");
                }
            _terminalOrdinaryStyles[model+"/"+name]=(style,initial,target,fade);
        }
    }

    private bool TerminalOrdinaryText(string name,string text,EnbRgb color)
    {
        string key=_terminalArt+"/"+name.ToUpperInvariant();
        if(!_terminalOrdinaryStyles.TryGetValue(key,out var definition))return false;
        //00544090/005B0BB0/005EC270 construct a fresh font collection through
        //006D1D80/006CE990. Mode0 text replacement in005ECBB0/00545190
        //preserves its clock; animated attachment updates preserve it too.
        if(!_terminalOrdinaryAlpha.TryGetValue(key,out var alpha))_terminalOrdinaryAlpha[key]=alpha=new();
        if(!_terminalOrdinaryScroll.TryGetValue(key,out var scroll))_terminalOrdinaryScroll[key]=scroll=new();
        var style=definition.Style;
        bool hovered=string.Equals(_hover,"terminal/"+name,StringComparison.OrdinalIgnoreCase) ||
            _terminalArt=="jnews00" && name.StartsWith("button",StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan(6),out int button) && _hover=="jobType/"+button;
        float opacity=alpha.Update(Environment.TickCount64,hovered,definition.Initial,definition.Target,definition.Fade,style.BlinkSpeed,style.RevertSpeed);
        EnbAttachedLabelWipe? opening=null;
        if(style.OpeningWipeRightSpeed>0)
        {
            if(!_terminalOrdinaryWipes.TryGetValue(key,out opening))_terminalOrdinaryWipes[key]=opening=new();
        }
        int first=_images.Count;
        AttachedText("terminalLabel/"+name.ToUpperInvariant(),text,style,scroll,color,opening,fixedLabel:true);
        for(int i=first;i<_images.Count;i++)_images[i]=_images[i] with {Opacity=opacity};
        _terminalOrdinaryDrawn.Add(key);
        _animatingText|=!alpha.OpeningComplete&&definition.Fade>0 || hovered&&style.BlinkSpeed>0 || opacity<1;
        return true;
    }
}
