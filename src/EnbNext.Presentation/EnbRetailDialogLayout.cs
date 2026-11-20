namespace EnbNext.Presentation;

public readonly record struct EnbDialogRect(float X, float Y, float Width, float Height);
public readonly record struct EnbDialogBorder(float X, float Y);

public sealed record EnbRetailDialogLayout(EnbDialogRect Window, EnbDialogRect Info,
    EnbDialogRect[] Lines, EnbDialogRect[] Options, EnbDialogRect[] Labels)
{
    //645510 nests one section in the window, then four option frames.
    //Each frame adds two surplus units (516A00);51F0F0 distributes them.
    public static EnbRetailDialogLayout MeasureMenu(float[] widths,float lineHeight,
        EnbDialogBorder outer,EnbDialogBorder section,EnbDialogBorder option)
    {
        float row=lineHeight+2*option.Y+2;
        float width=widths.DefaultIfEmpty(0).Max()+2*option.X+2+2*section.X+2+2*outer.X+2;
        float height=widths.Length*row+2*section.Y+2+2*outer.Y+2;
        var info=new EnbDialogRect(outer.X,outer.Y,width-2*outer.X,height-2*outer.Y);
        float step=widths.Length==0?0:(info.Height-2*section.Y)/widths.Length;
        var rows=Enumerable.Range(0,widths.Length).Select(i=>new EnbDialogRect(
            info.X+section.X,info.Y+section.Y+i*step,info.Width-2*section.X,step)).ToArray();
        var labels=rows.Select(r=>new EnbDialogRect(r.X+option.X,r.Y+option.Y,r.Width-2*option.X,r.Height-2*option.Y)).ToArray();
        return new(new(0,0,width,height),info,[],rows,labels);
    }
    // 6ABC60 inserts stretchable text widgets into one framed column.
    public static (EnbDialogRect Window,EnbDialogRect[] Lines) MeasureTextFrame(float[] widths,float lineHeight,EnbDialogBorder border)
    {
        float width=widths.DefaultIfEmpty(0).Max()+2*border.X+2;
        float height=widths.Length*lineHeight+2*border.Y+2;
        float step=widths.Length==0?0:(height-2*border.Y)/widths.Length;
        return(new(0,0,width,height),Enumerable.Range(0,widths.Length)
            .Select(i=>new EnbDialogRect(border.X,border.Y+i*step,width-2*border.X,step)).ToArray());
    }
    //00517C50 centers about the requested point and clamps each leading edge
    //first, then its trailing edge. The five-unit inset is00AED3F8.
    public static EnbDialogRect Place(float width,float height,float centerX,float centerY)
    {
        float x=centerX-width*.5f,y=centerY-height*.5f;
        if(x<5)x=5;else if(x+width>635)x=635-width;
        if(y<5)y=5;else if(y+height>475)y=475-height;
        return new(x,y,width,height);
    }

    // 00587D50/00588DC0 build rows of stretchable text widgets, nested in
    // frames. 00516A00 adds two units beyond the authored borders; 00516AE0
    // subtracts only the borders, leaving that surplus for the row/column
    // layouts (00518D20/0051F0F0). Listener00519240/0051F600 supplies counts.
    public static EnbRetailDialogLayout Measure(float[] lineWidths, float lineHeight,
        float[] optionWidths, float optionHeight, EnbDialogBorder outer,
        EnbDialogBorder info, EnbDialogBorder option)
    {
        float infoWidth = lineWidths.DefaultIfEmpty(0).Max() + 2 * info.X + 2;
        float infoHeight = lineWidths.Length * lineHeight + 2 * info.Y + 2;
        float[] minimumOptions = optionWidths.Select(w => w + 2 * option.X + 2).ToArray();
        float optionsWidth = minimumOptions.Sum();
        float optionsHeight = optionHeight + 2 * option.Y + 2;
        float width = Math.Max(infoWidth, optionsWidth) + 2 * outer.X + 2;
        float height = infoHeight + optionsHeight + 2 * outer.Y + 2;
        float inner = width - 2 * outer.X;
        // The outer column gives one surplus unit to each of its two rows.
        infoHeight += 1;
        optionsHeight += 1;
        var infoRect = new EnbDialogRect(outer.X, outer.Y, inner, infoHeight);
        var lines = new EnbDialogRect[lineWidths.Length];
        float lineStep = lines.Length == 0 ? 0 : (infoHeight - 2 * info.Y) / lines.Length;
        for (int i = 0; i < lines.Length; i++)
            lines[i] = new(outer.X + info.X, outer.Y + info.Y + i * lineStep,
                inner - 2 * info.X, lineStep);
        var options = new EnbDialogRect[optionWidths.Length];
        var labels = new EnbDialogRect[options.Length];
        float surplus = options.Length == 0 ? 0 : (inner - optionsWidth) / options.Length;
        float x = outer.X, y = outer.Y + infoHeight;
        for (int i = 0; i < options.Length; i++)
        {
            float w = minimumOptions[i] + surplus;
            options[i] = new(x, y, w, optionsHeight);
            labels[i] = new(x + option.X, y + option.Y, w - 2 * option.X, optionsHeight - 2 * option.Y);
            x += w;
        }
        return new(new(0, 0, width, height), infoRect, lines, options, labels);
    }
}
