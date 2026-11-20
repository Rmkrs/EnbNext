using EnbNext.Assets;

namespace EnbNext.Presentation;

/// <summary>Retained authored text and GPU upload pixels, independent of a UI framework.</summary>
public class EnbRetailText : IDisposable
{
    private string? _text;
    private EnbRetailFontFace? _font;
    private byte _r, _g, _b;
    public string? Text => _text;
    public EnbRetailFontFace? Font => _font;
    public EnbRgb Tint => new(_r / 255f, _g / 255f, _b / 255f);
    public EnbUiRaster? Raster { get; private set; }
    public double Width { get; private set; }
    public double Height { get; private set; }
    private EnbUiRect? _inkBounds;
    public EnbUiRect InkBounds
    {
        get
        {
            if(_inkBounds is {} bounds)return bounds;
            if(Raster is null)return default;
            int left=Raster.Width, top=Raster.Height, right=0, bottom=0;
            var pixels=Raster.BgraPixels.AsSpan();
            for(int y=0;y<Raster.Height;y++)for(int x=0;x<Raster.Width;x++)
                if(pixels[(y*Raster.Width+x)*4+3]!=0)
                {left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x+1);bottom=Math.Max(bottom,y+1);}
            var ink=right>left?new EnbUiRect(left,top,right-left,bottom-top):default;
            _inkBounds=ink;return ink;
        }
    }
    public bool IsVisible { get; set; }

    public void SetText(string? value, EnbRetailFontFace? face, EnbRgb tint)
    {
        if (string.IsNullOrWhiteSpace(value) || face is null)
        {
            Clear(); IsVisible = false; return;
        }
        byte red = ToByte(tint.R), green = ToByte(tint.G), blue = ToByte(tint.B);
        if (Raster is not null && string.Equals(_text, value, StringComparison.Ordinal) &&
            ReferenceEquals(_font, face) && _r == red && _g == green && _b == blue)
        {
            IsVisible = true; return;
        }
        Clear(); _text = value; _font = face; _r = red; _g = green; _b = blue;
        Raster = face.RenderText(value, tint);
        Width = Math.Max(1, Raster.Width * face.Scale);
        Height = Math.Max(1, Raster.Height * face.Scale);
        IsVisible = true;
    }

    public void SetDisplayScale(double horizontal, double vertical)
    {
        if (Raster is null || _font is null) return;
        Width = Math.Max(1, Raster.Width * _font.Scale * horizontal);
        Height = Math.Max(1, Raster.Height * _font.Scale * vertical);
    }

    public double AppliedUiScale { get; private set; } = 1;

    /// <summary>Shrinks the drawn label uniformly by <paramref name="fit"/> (at most 1).</summary>
    public void Shrink(double fit)
    {
        if (fit >= 1 || fit <= 0) return;
        Width *= fit; Height *= fit;
    }

    // Preserve the historical 100% path. At enlarged sizes, a fixed gadget
    // limits the entire label uniformly, including its height.
    public void FitTo(EnbUiSize available, double uiScale)
    {
        AppliedUiScale = uiScale;
        if (uiScale == 1 || Width <= 0 || Height <= 0) return;
        // Zero dimensions describe an anchor-only extent, not a fixed frame.
        double fit = Math.Min(1, Math.Min(available.Width > 0 ? available.Width / Width : 1,
            available.Height > 0 ? available.Height / Height : 1));
        Width *= fit; Height *= fit; AppliedUiScale *= fit;
    }

    // All labels in a group share the font's complete vertical extent. Only
    // unusually wide strings may shrink further, keeping the group's baseline.
    public double FitLineTo(EnbUiRect available, EnbUiPoint anchor, double lineHeight, double uiScale, bool center, bool right)
    {
        if(Raster is null || _font is null)return available.Top;
        double unit=Height/Raster.Height;
        double fit=Math.Min(1,lineHeight/((_font.LineInkBottom-_font.LineInkTop+1)*unit));
        double lineUnit=unit*fit;
        double baseline=anchor.Y+(_font.Baseline-(_font.LineInkTop+_font.LineInkBottom)/2d)*lineUnit;
        baseline=Math.Clamp(baseline,available.Top+(_font.Baseline-_font.LineInkTop+.5)*lineUnit,
            Math.Max(available.Top+(_font.Baseline-_font.LineInkTop+.5)*lineUnit,available.Bottom-(_font.LineInkBottom+.5-_font.Baseline)*lineUnit));
        double originX=center?-Width/2:right?-Width:0;
        double left=originX+(InkBounds.Left-.5)*unit, rightEdge=originX+(InkBounds.Right+.5)*unit;
        if(left<0)fit=Math.Min(fit,(anchor.X-available.Left)/-left);
        if(rightEdge>0)fit=Math.Min(fit,(available.Right-anchor.X)/rightEdge);
        fit=Math.Clamp(fit,0,1);
        FitTo(new(Width*fit,Height*fit),uiScale);
        return baseline-_font.Baseline*unit*fit;
    }

    public void Clear()
    {
        Raster = null; _inkBounds = null; _text = null; _font = null; Width = Height = 0;
    }

    public void Dispose() => Clear();
    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
