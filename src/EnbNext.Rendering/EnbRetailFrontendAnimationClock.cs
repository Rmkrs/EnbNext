using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>CharacterView's 67AE20/67AE60, 67AF70 and consuming 67AEA0 latch.</summary>
public sealed class EnbRetailFrontendAnimationClock(int frames,float rate,bool reverse)
{
    public float Progress { get; private set; }
    public float Frame { get; private set; }
    private bool _completed;

    public void Advance(uint milliseconds)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        float last=frames-1;
        if(Progress<last)
        {
            float seconds=(float)((R)milliseconds*(R).001f);
            Progress=Progress==0?1:(float)((R)rate*(R)seconds+(R)Progress);
            if(Progress>=last){Progress=last;_completed=true;}
        }
        Frame=reverse?(float)((R)last-(R)Progress):Progress;
    }

    public bool ConsumeCompletion()
    {
        bool completed=_completed;_completed=false;return completed;
    }
}
