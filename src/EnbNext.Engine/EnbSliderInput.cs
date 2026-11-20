using EnbNext.Presentation;

namespace EnbNext.Engine;

internal enum EnbScrollbarPart { None, Up, PageUp, Thumb, PageDown, Down }

/// <summary>0066EF00/0066F330/00670440: one slider's axis, value, capture and repeat owner.</summary>
internal sealed class EnbSliderInput
{
    private readonly EnbUiRect track, bounds;
    private readonly bool horizontal, singlePrecision;

    public EnbSliderInput(EnbUiRect track, EnbUiRect bounds, bool horizontal = false, bool singlePrecision = false, double minimum = 0)
    {
        Minimum = minimum; Value = minimum; Maximum = minimum;
        this.horizontal = horizontal; this.singlePrecision = singlePrecision;
        this.track = horizontal ? new(track.Y, track.X, track.Height, track.Width) : track;
        this.bounds = horizontal ? new(bounds.Y, bounds.X, bounds.Height, bounds.Width) : bounds;
        ThumbLength = this.track.Height;
    }

    private EnbUiPoint Axis(EnbUiPoint point) => horizontal ? new(point.Y, point.X) : point;
    public double Minimum { get; }
    public double Value { get; private set; }
    public int Offset => (int)Value;
    public double Maximum { get; private set; }
    public double Page { get; private set; }
    public double ThumbLength { get; private set; }
    public double ThumbStart => track.Y + (Maximum > Minimum ? Travel * (Value - Minimum) / (Maximum - Minimum) : 0);
    private double Travel => Math.Max(0, track.Height - ThumbLength);
    public bool NotchChanged { get; private set; }
    public EnbScrollbarPart Pressed { get; private set; }
    public long RepeatAt { get; private set; }
    private double dragY, dragValue;

    public void Configure(int total, int page)
    {
        NotchChanged = false;
        Maximum = Minimum + Math.Max(0, total - page); Page = page;
        ThumbLength = Math.Max(16, track.Height * (total > 0 ? Math.Min(1, (double)page / total) : 1));
        if (Value > Maximum) SetValue(Maximum);
        if (Maximum == Minimum) Cancel();
    }

    public void SetValue(double value)
    {
        double next = Math.Clamp(singlePrecision ? (float)value : value, Minimum, Maximum);
        NotchChanged = Math.Round(next) != Math.Round(Value);
        Value = next;
    }

    public EnbScrollbarPart CurrentHit { get; private set; }
    public EnbScrollbarPart PreviousHit { get; private set; }
    public void Query(EnbUiPoint point) { PreviousHit=CurrentHit;CurrentHit=Hit(point); }
    public EnbScrollbarPart Hit(EnbUiPoint point)
    {
        point = Axis(point);
        if (Maximum == Minimum || point.X < bounds.X || point.X > bounds.X + bounds.Width ||
            point.Y < bounds.Y || point.Y > bounds.Y + bounds.Height) return EnbScrollbarPart.None;
        if (point.Y < track.Y) return EnbScrollbarPart.Up;
        if (point.Y < ThumbStart) return EnbScrollbarPart.PageUp;
        if (point.Y < ThumbStart + ThumbLength) return EnbScrollbarPart.Thumb;
        return point.Y < track.Y + track.Height ? EnbScrollbarPart.PageDown : EnbScrollbarPart.Down;
    }

    public bool Begin(EnbScrollbarPart part, EnbUiPoint point, long now)
    {
        Cancel();
        if (Maximum == Minimum || part == EnbScrollbarPart.None) return false;
        Pressed = part; RepeatAt = now + 500;
        dragY = Axis(point).Y - ThumbStart; dragValue = Value;
        return part != EnbScrollbarPart.Thumb && Step();
    }

    public bool Advance(EnbUiPoint point, long now)
    {
        if (Pressed is EnbScrollbarPart.None or EnbScrollbarPart.Thumb || now < RepeatAt || Hit(point) != Pressed) return false;
        // Native adds the interval to the previous deadline, at most once per frame.
        RepeatAt += 100;
        return Step();
    }

    public bool Move(EnbUiPoint point)
    {
        if (Pressed != EnbScrollbarPart.Thumb) return false;
        point = Axis(point);
        if (point.X < bounds.X - 32 || point.X > bounds.X + bounds.Width + 32 ||
            point.Y < bounds.Y - 32 || point.Y > bounds.Y + bounds.Height + 32) SetValue(dragValue);
        else SetValue(Travel > 0 ? (point.Y - dragY - track.Y) / Travel * (Maximum - Minimum) + Minimum : Minimum);
        return true;
    }

    private bool Step()
    {
        double step = Pressed switch { EnbScrollbarPart.Up => -1, EnbScrollbarPart.PageUp => -Page,
            EnbScrollbarPart.PageDown => Page, EnbScrollbarPart.Down => 1, _ => 0 };
        SetValue(Value + step); return true;
    }

    public void Cancel() { Pressed = EnbScrollbarPart.None; RepeatAt = 0; dragY = dragValue = 0; }
}
