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
    private sealed record RecoveryAnswerDefinition(EnbAttachedTextStyle Style,float Spacing,float Delay,float Wait,float Speed,float Toggle,EnbRgb Color,EnbRgb Alternate);
    private sealed record RecoveryAnswerRows(string Text,Size Size,EnbRetailFontFace Font,double Scale,string[] Lines,EnbWrappedLabelRow[] Rows,EnbAttachedLabelWipe[] Wipes)
    {
        public long? Tick;
        public readonly EnbAttachedLabelToggle Color=new();
    }
    private readonly Dictionary<int,RecoveryAnswerDefinition> _recoveryAnswerDefinitions=[];
    private readonly Dictionary<int,RecoveryAnswerRows> _recoveryAnswers=[];
    private void LoadRecoveryAnswers(EnbAuthoredUiAsset asset)
    {
        _recoveryAnswerDefinitions.Clear();_recoveryAnswers.Clear();
        var effects=EnbRetailIniDocument.Load(Path.Combine(_assets!.Paths.ClientIniDirectory,"fontart.ini"));
        for(int i=0;i<5;i++)
        {
            if(!_conversationLayouts["talk"].TryResolve($"WRAP_Answer{i:00}_TLI01A_AV",out var label))continue;
            float Number(string key)=>float.Parse(label.Properties.GetValueOrDefault(key,"0"),CultureInfo.InvariantCulture);
            float speed=0,toggle=0;
            foreach(var effect in effects.EnumerateSections().Select(s=>s.Value))
                if(label.Properties.GetValueOrDefault("MouseOver","").Split(',').Any(id=>id.Trim()==effect.GetValueOrDefault("EffectID")))
                {
                    if(effect.GetValueOrDefault("EffectType")=="SCROLL" && effect.GetValueOrDefault("Direction")=="UP")speed=float.Parse(effect["Speed"],CultureInfo.InvariantCulture);
                    if(effect.GetValueOrDefault("EffectType")=="TOGGLE")toggle=float.Parse(effect["Speed"],CultureInfo.InvariantCulture);
                }
            _recoveryAnswerDefinitions[i]=new(EnbAttachedTextStyle.Load(_assets,asset,label,Matrix4x4.Identity),Number("Spacing"),Number("TimeSpacing"),Number("Wait"),speed,toggle,
                _conversationColors[label.Properties["Color"]],_conversationColors[label.Properties["AltColor"]]);
        }
    }
    // 551FB0/5524F0 resolve WRAP_Answer owners; 552980 assigns +FC texts.
    // Their rows use the same 6CF930/6D60C0 clock as frontend wrapped labels.
    private void RecoveryAnswer(int index,string id,string text)
    {
        if(!_recoveryAnswerDefinitions.TryGetValue(index,out var definition)||Font(definition.Style.Font) is not {} font)return;
        var style=definition.Style;
        float xUnit=(float)(font.Scale*640/_size.Width),yUnit=(float)(font.Scale*480/_size.Height);
        float height=font.CellHeight*yUnit,step=height*definition.Spacing;
        if(!_recoveryAnswers.TryGetValue(index,out var state)||state.Text!=text||state.Size!=_size||!ReferenceEquals(state.Font,font)||state.Scale!=_scale)
        {
            float width=(float)style.Clip.Width-font.MeasureWidth("...")*xUnit;
            var lines=EnbRetailDialogText.Wrap(text.Length==0?" ":text,width,c=>font.CharacterAdvance(c)*xUnit);
            state=new(text,_size,font,_scale,lines,lines.Select((_,i)=>new EnbWrappedLabelRow((int)((definition.Wait+i*definition.Delay)*1000))).ToArray(),lines.Select(_=>new EnbAttachedLabelWipe()).ToArray());
            _recoveryAnswers[index]=state;
        }
        long now=Environment.TickCount64;
        int delta=state.Tick is {} tick?(int)Math.Max(0,now-tick):0;state.Tick=now;
        var clip=Screen(style.Clip);
        bool hovered=_point.X>clip.Left&&_point.X<clip.Right&&_point.Y>clip.Top&&_point.Y<clip.Bottom;
        float limit=Math.Max(0,Math.Abs(step)*state.Lines.Length-(int)style.Clip.Height);
        var color=state.Color.Update(now,hovered,definition.Color,definition.Alternate,definition.Toggle);
        for(int i=0;i<state.Lines.Length;i++)
        {
            var sample=state.Rows[i].Advance(delta,hovered,limit,definition.Speed);
            if(!sample.Visible){_animatingText=true;continue;}
            double y=style.Anchor.Y-i*step-sample.Offset;
            bool ellipsis=state.Rows[i].UpdateEllipsis((float)-y,(float)-style.Clip.Bottom,(int)height,i+1==state.Lines.Length);
            AttachedText(id+"/"+i,state.Lines[i]+(ellipsis?"...":""),style with {Anchor=new(style.Anchor.X,y),ScrollSpeed=0},new EnbAttachedLabelScroll(),color,state.Wipes[i]);
            _animatingText|=hovered||!state.Wipes[i].Complete;
        }
    }
}
