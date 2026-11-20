namespace EnbNext.Presentation;

/// <summary>006CB25F WIPE RIGHT and 006CC1DC timed STAY for wrapped mission rows.</summary>
public sealed class EnbWrappedLabelOpening(float speed,float staySeconds)
{
    public bool Active {get;private set;}=speed>0;
    public int Advance(int clock,int width)
    {
        if(!Active)return width;
        int extent=Math.Max(1,(int)(clock*speed*.01f));
        // Timed STAY changes phase independently of wipe completion; a
        // negative Time waits for completion (006CC18B).
        if(staySeconds<0?extent>=width:clock>=(int)(staySeconds*1000))
        {Active=false;return width;}
        return Math.Min(width,extent);
    }
}
