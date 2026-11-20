using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>00781EA0 selection and 00783B50 idle-clock gates; no speech ownership.</summary>
public sealed class EnbRetailAvatarIdle
{
    public int FidgetDelay { get; }
    public float Elapsed { get; private set; }
    public int Jitter { get; private set; }
    public int BoredCount { get; private set; }
    public int Breath { get; private set; }
    public bool ResetHeadTargets { get; private set; }
    public bool StandStill { get; set; }
    public bool SuppressFidgets { get; set; }
    public bool HeadMotionEnabled { get; set; } = true;
    public int Posture { get; set; }

    public EnbRetailAvatarIdle(EnbRetailIniDocument personality,Func<int,int,int>? random=null)
    {
        // 0078FCB0 asks for FidgetDelay, not the installed typo FigetDelay.
        FidgetDelay=personality.TryGetSection("HEADER",out var header) &&
            int.TryParse(header.GetValueOrDefault("FidgetDelay"),out int delay)?delay:45;
        Jitter=(random??EnbRetailPresentationRandom.Shared.Next)(0,FidgetDelay/2);
    }
    public void Advance(float seconds,bool idle)
    {
        if(StandStill || SuppressFidgets)Elapsed=0;
        else if(idle && Posture==0 && HeadMotionEnabled)Elapsed+=seconds;
    }
    // 7826A0 and standing-command branch782200 reset +22C/+244,
    // without replacing the authored jitter or breath history.
    public void ResetForActivity(){Elapsed=0;BoredCount=0;}
    public string Next(Func<int,int,int> random)
    {
        ResetHeadTargets=false;
        if(Posture!=0)return "sit_breathe";
        if(Elapsed>unchecked(FidgetDelay+Jitter))
        {
            string tag=BoredCount<4?"bored"+random(1,4):"bored";
            BoredCount=BoredCount<4?BoredCount+1:0;
            Elapsed=0;Jitter=random(0,FidgetDelay/2);ResetHeadTargets=true;
            return tag;
        }
        if(StandStill)return "stand_still";
        if(Breath==1)
        {
            Breath=1;
            while(Breath<4 && random(0,1000)*.001f>=.7f)Breath++;
        }
        else Breath=1;
        return "breathe"+Breath;
    }
}
