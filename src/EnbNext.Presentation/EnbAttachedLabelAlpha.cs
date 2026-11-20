namespace EnbNext.Presentation;

/// <summary>Authored FADE/STAY COMPLETE opening followed by BLINK and REVERT.
/// Retail 006CA160, 006CA30D, 006CBABE and 006CC18B; hover executes before opening.</summary>
public sealed class EnbAttachedLabelAlpha
{
    private long? _tick;
    private long? _paused;
    private long _entered, _fadeElapsed;
    private bool _hovered, _alphaEffect;
    public bool OpeningComplete { get; private set; }
    public float Opacity { get; private set; }=1;

    public void Pause(long now) { if(_tick.HasValue)_paused??=now; }

    public float Update(long now,bool hovered,float initial,float target,float fadeSpeed,float blinkSpeed,float revertSpeed)
    {
        if(_paused is {} paused)
        {
            long gap=Math.Max(0,now-paused);
            if(_tick.HasValue)_tick+=gap;
            _entered+=gap;_paused=null;
        }
        long delta=_tick.HasValue?Math.Max(0,now-_tick.Value):0;_tick=now;
        bool blink=hovered&&blinkSpeed>0;
        if(blink)
        {
            if(!_hovered)_entered=now;
            long period=(long)(500f/blinkSpeed);
            if(period>0){long phase=(now-_entered)%period;float half=period*.5f;Opacity=phase<=half?phase/half:(period-phase)/half;}
        }
        bool opening=!OpeningComplete&&fadeSpeed>0;
        if(opening)
        {
            // +284 is sampled before this frame's delta is added.
            double step=_fadeElapsed*(double)fadeSpeed*BitConverter.Int32BitsToSingle(0x3a03126f);
            Opacity=(float)(target>initial?Math.Min(target,initial+step):Math.Max(target,initial-step));
            _fadeElapsed+=delta;
            // BLINK also sets +404, consumed by the following STAY COMPLETE.
            OpeningComplete=blink||Opacity==target;
        }
        else if(!blink&&!_alphaEffect&&revertSpeed>0)Opacity=Math.Min(1,Opacity+delta*revertSpeed*.001f);
        _alphaEffect=blink||opening;_hovered=hovered;
        return Opacity;
    }
}
