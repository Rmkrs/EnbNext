namespace EnbNext.Presentation;

/// <summary>Retail attached-label BLINK and REVERT/SCROLL_REVERT alpha.
/// 006CD180, 006CA160, 006CBABE/006CBC78, 006CCE60.</summary>
public sealed class EnbAttachedLabelHover
{
    private long _tick=-1, _entered;
    private bool _hovered;
    private string? _text;
    public float Opacity { get; private set; }=1;

    public void Clear(){_tick=-1;_entered=0;_hovered=false;_text=null;Opacity=1;}

    public float Update(string text,bool hovered,long tick,float blinkSpeed,float revertSpeed)
    {
        if(_text!=text){Clear();_text=text;}
        long elapsed=_tick<0?0:Math.Max(0,tick-_tick);_tick=tick;
        if(hovered && blinkSpeed>0)
        {
            if(!_hovered)_entered=tick;
            long period=(long)(500f/blinkSpeed);
            if(period>0)
            {
                long position=(tick-_entered)%period;
                float half=period*.5f;
                Opacity=position<=half?position/half:(period-position)/half;
            }
        }
        // The hover effect sets its frame counter to2; the per-frame cleanup
        // decrements it. The first frame outside still blocks REVERT.
        else if(!_hovered && revertSpeed>0)Opacity=Math.Min(1,Opacity+elapsed*revertSpeed*.001f);
        _hovered=hovered;
        return Opacity;
    }
}
