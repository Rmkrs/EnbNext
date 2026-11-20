using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private EnbAttachedTextStyle? _biographyNameStyle, _skillTitleStyle;
    private readonly EnbAttachedLabelScroll _biographyNameScroll = new(), _skillTitleScroll = new();

    private void AttachedText(string id, string text, EnbAttachedTextStyle? style,
        EnbAttachedLabelScroll scroll, EnbRgb color, EnbAttachedLabelWipe? opening = null, bool fixedLabel = false)
    {
        if(style is null || Font(style.Font) is not {} font)return;
        if(!_labels.TryGetValue(id,out var label))_labels[id]=label=new();
        label.SetText(text,font,color);label.SetDisplayScale(1,1);
        if(fixedLabel)label.FitTo(Screen(style.Clip).Size,_scale);
        float width=(float)(label.Width*640/_size.Width);
        float offset=scroll.Update(text,Environment.TickCount64,width,style.HalfWidth,style.ScrollSpeed);
        _animatingText|=style.ScrollSpeed!=0&&width>(int)(style.HalfWidth*1.9f);
        var anchor=Screen(new Rect(style.Anchor.X+offset,style.Anchor.Y,0,0));
        var bounds=new Rect(anchor.X-(style.Center?label.Width/2:style.Right?label.Width:0),anchor.Y-label.Height/2,label.Width,label.Height);
        var visible=bounds.Intersect(Screen(style.Clip));
        if(opening is not null && style.OpeningWipeRightSpeed>0)
        {
            int extent=opening.Update(Environment.TickCount64,(int)Math.Round(label.Width*_scale),style.OpeningWipeRightSpeed);
            visible=visible.Intersect(new Rect(bounds.X,bounds.Y,extent/_scale,bounds.Height));
            _animatingText|=!opening.Complete;
        }
        if(label.Raster is {} raster&&visible.Width>0&&visible.Height>0)
        {
            _images.Add(new(raster,(bounds)){Clip=(visible)});
            if(style.Underline && text.Length!=0)AttachedUnderline(id,font,color,visible);
        }
    }

    private void AttachedUnderline(string id,EnbRetailFontFace font,EnbRgb color,Rect visible)
    {
        // 006D5170 creates a separate "_" texture; 006D7617..006D7A78
        // stretches its central 20%..80% over the clipped text quad.
        if(visible.Width<=0 || visible.Height<=0)return;
        string key=id+"/underline";
        if(!_labels.TryGetValue(key,out var underline))_labels[key]=underline=new();
        underline.SetText("_",font,color);
        if(underline.Raster is {} rule)
            _images.Add(new(rule,(new Rect(visible.X-visible.Width/3,visible.Y,visible.Width/0.6,visible.Height))){Clip=(visible)});
    }
}
