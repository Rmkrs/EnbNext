namespace EnbNext.Presentation;

/// <summary>598200/5982A0 countdown,5935C0 expiry and6609A0 retained forced flash.</summary>
public sealed class EnbGroupAttentionFlash
{
    public float Remaining {get;private set;}
    public bool Forced {get;private set;}
    public bool HoverLatch {get;private set;}
    public float Emissive {get;private set;}=1;
    public void Request(){Remaining=15;Forced=true;}
    public void Clear(){Remaining=0;RebuildWidget();}
    // 5934A0 destroys the widget, not the member's +20 countdown.
    public void RebuildWidget(){Forced=false;ReplaceSource();}
    public void ReplaceSource(){HoverLatch=false;Emissive=1;}
    public void Advance(uint elapsed)
    {
        if(Remaining<=0)return;
        float delta=(float)elapsed*.001f;
        Remaining=Remaining-delta;
        if(Remaining<0)Clear();
    }
    // Returns whether the native material setter is called this update.
    public bool UpdateMaterial(bool hovered,uint time)
    {
        if(!Forced)return false;
        if(hovered)
        {
            if(HoverLatch)return false;
            HoverLatch=true;Emissive=1;return true;
        }
        if(HoverLatch){HoverLatch=false;return false;}
        Emissive=EnbWidgetFlash.Emissive(time);return true;
    }
}
