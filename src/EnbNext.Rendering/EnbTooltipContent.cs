using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Rendering;

public static class EnbTooltipContent
{
    // 6ABC60 wraps into at most twenty text widgets; 516A00/516AE0 size
    // their framed column. Keep empty rows in the layout, too.
    public static EnbUiTooltipDraw Create(EnbAuthoredTooltipFrame frame,
        EnbRetailFontFace font,string text,EnbUiSize viewport,double scaling,EnbRgb color,
        EnbUiPoint pointer,Func<int,EnbRetailText> label,EnbUiRect? gadgetBounds=null)
    {
        double sx=viewport.Width/640,sy=viewport.Height/480;
        float fx=(float)(font.Scale/sx),fy=(float)(font.Scale/sy);
        var wrapped=EnbRetailDialogText.WrapTooltip(text,c=>font.CharacterAdvance(c)*fx);
        var layout=EnbRetailDialogLayout.MeasureTextFrame(wrapped.Lines.Select(s=>font.MeasureWidth(s)*fx).ToArray(),
            font.CellHeight*fy,new((float)frame.Definition.BorderX,(float)frame.Definition.BorderY));
        double w=layout.Window.Width*sx,h=layout.Window.Height*sy;
        var images=ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        for(int i=0;i<wrapped.Lines.Length;i++)
        {
            var part=label(i);part.SetText(wrapped.Lines[i],font,color);part.SetDisplayScale(1,1);
            var row=layout.Lines[i];
            if(part.Raster is {} raster)images.Add(new(raster,new(row.X*sx+(row.Width*sx-part.Width)/2,
                row.Y*sy+(row.Height*sy-part.Height)/2,part.Width,part.Height)));
        }
        var p=EnbCurrentRetailTooltipLayout.Place(pointer.X,pointer.Y,w,h,viewport.Width,viewport.Height);
        if(gadgetBounds is {} gadget)
        {
            // CharacterView's cursor getter is null: 6ABE90 uses the model
            // box center X and top Y, once when the tooltip is first shown.
            var placed=EnbRetailDialogLayout.Place(layout.Window.Width,layout.Window.Height,
                (float)(gadget.Center.X/sx),(float)(gadget.Top/sy-layout.Window.Height/2));
            p=(placed.X*sx,placed.Y*sy);
        }
        return new(frame,new(p.X,p.Y,w,h),new(layout.Window.Width,layout.Window.Height)){Lines=images.ToImmutable(),Text=text,Pointer=pointer,Gadget=gadgetBounds};
    }
}
