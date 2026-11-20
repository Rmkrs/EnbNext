using EnbNext.Assets;
using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>6425D0 speaker publication and 63AC60's retained name timer.</summary>
public sealed class EnbStationSpeakerLabel(string firstName, string fullName)
{
    public string FirstName { get; } = firstName;
    public string Text { get; private set; } = fullName;
    public float Remaining { get; private set; }
    public bool Flash { get; private set; }
    public EnbRgb Color { get; private set; } = new(.33f,.33f,.99f);
    public void Speak(EnbRgb color)
    { Remaining = 2; Text = "\".....\""; Color = color; Flash = true; }
    public void Advance(uint milliseconds, bool paused, EnbRgb normalColor)
    {
        if (paused) return;
        using var precision = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        if (Remaining > 0)
        {
            R next = (R)Remaining - (R)milliseconds * (R).001f;
            Remaining = (float)next;
            // Strictly negative, not <=0: exact zero retains text/flash.
            if (next < (R)0) { Remaining = 0; Text = FirstName; Flash = false; }
        }
        if (Remaining <= 0) Color = normalColor;
    }
    public static float AdvanceFlash(float alpha, uint milliseconds)
    {
        using var precision = new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        R delta = (R)milliseconds * (R).001f;
        R next = delta + delta + (R)alpha;
        return (float)(next > (R)1 ? next - (R)1 : next);
    }
}
