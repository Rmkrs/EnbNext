using EnbNext.Assets;

namespace EnbNext.Presentation;

/// <summary>006CA0C0 case 15 and 006CCE60: approach AltColor, restore Color on exit.</summary>
public sealed class EnbAttachedLabelToggle
{
    private long? _tick;
    private EnbRgb? _color;
    public void Pause()=>_tick=null;
    public EnbRgb Update(long now,bool hovered,EnbRgb basis,EnbRgb alternate,float speed)
    {
        static float Native(float channel)=>MathF.Round(channel*255)*BitConverter.Int32BitsToSingle(0x3b80806e);
        var normal=new EnbRgb(Native(basis.R),Native(basis.G),Native(basis.B));
        long delta=_tick is {} tick?Math.Max(0,now-tick):0;_tick=now;
        var current=_color??normal;
        float step=(float)(delta*(double)speed*BitConverter.Int32BitsToSingle(0x3c23d70a));
        float Move(float value,float target)=>value>target?Math.Max(target,value-step):Math.Min(target,value+step);
        _color=hovered?new(Move(current.R,Native(alternate.R)),Move(current.G,Native(alternate.G)),Move(current.B,Native(alternate.B))):normal;
        return _color.Value;
    }
}
