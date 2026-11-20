namespace EnbNext.Presentation;

/// <summary>5935C0 positive float countdown and expiry transition.</summary>
public sealed class EnbGroupSymbolLifetime(float seconds=5)
{
    public float Remaining {get;private set;}=seconds;
    public bool Expired {get;private set;}
    public void Advance(uint elapsed)
    {
        if(Remaining<=0)return;
        float delta=(float)elapsed*.001f;
        Remaining=Remaining-delta;
        if(Remaining<0){Remaining=0;Expired=true;}
    }
}
