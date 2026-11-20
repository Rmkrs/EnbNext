namespace EnbNext.Rendering;

/// <summary>The two retained body HAnim slots at avatar+C0/C4, 007831C0 and
/// 00783620. Selection occurs after the frame update; binding does not seek the
/// render object until the next Advance call.</summary>
public sealed class EnbRetailAvatarBodyClock
{
    public sealed record Clip(string Name,uint Frames,float Rate);
    public sealed record Pose(Clip Primary,float PrimaryFrame,Clip? Secondary,float SecondaryFrame,float Weight);
    private sealed class Slot(Clip clip)
    {
        public Clip Clip=clip;
        public float Frame;
        public bool Complete;
        public float Target;
        public uint Flags=uint.MaxValue;
        public float? Advance(float delta)
        {
            if(Complete)return null;
            float end=Clip.Frames-1;
            float target=Flags==uint.MaxValue?end:Math.Clamp((Flags&1)==0?end*Target:Target,0,end);
            if(Flags!=uint.MaxValue && (Flags&4)!=0)Frame=target;
            if(Frame>target)Frame=Math.Max(target,Frame-Clip.Rate*delta);
            else if(Frame<target)Frame=Math.Min(target,Frame+Clip.Rate*delta);
            if(Frame==target)Complete=Flags==uint.MaxValue || (Flags&2)==0;
            //783620 divides by this update's clamped target, including a
            //controlled partial/reverse target, before testing contacts.
            return Frame/target;
        }
    }
    private Slot? primary,secondary;
    private float blendDuration,blendRemaining;
    public Pose? Submitted {get;private set;}
    public bool CanSelect=>secondary is null && (primary is null || primary.Complete);
    public float PrimaryFrame=>primary?.Frame??0;
    public float SecondaryFrame=>secondary?.Frame??0;
    public bool Blending=>secondary is not null;
    public Clip? SelectedClip=>secondary?.Clip??primary?.Clip;
    public bool Controlled=>(secondary??primary) is {Flags:not uint.MaxValue};
    // 7899F0/789C10 target C4 when present, otherwise C0. A negative target
    // captures the current frame and switches from normalized to absolute.
    public void SetTarget(float target,uint flags)
    {
        if((secondary??primary) is not {} slot)return;
        slot.Target=target<0?slot.Frame:target;
        slot.Flags=target<0?flags|1:flags;
    }
    // 783620 tests contacts only while advancing a lone, unfinished slot,
    // before blend promotion or the caller's next-action selection.
    public float? AdvancedSingleProgress {get;private set;}
    public void Reset(){primary=secondary=null;Submitted=null;blendDuration=blendRemaining=0;}
    // Bridge an explicitly positioned body owner (editor / movement controller)
    // into the same retained slots before ordinary actor playback resumes.
    public void Position(Clip clip,float frame)
    {
        primary=new(clip){Frame=frame,Complete=frame>=clip.Frames-1};secondary=null;
        blendDuration=blendRemaining=0;Submitted=new(clip,frame,null,0,0);
    }
    public void Bind(Clip? clip,bool replace=false)
    {
        bool blend=primary is not null && !replace;
        secondary=null;
        if(!blend)primary=null;
        // A failed lookup leaves the selected slot empty. It must not erase
        // a usable primary when the attempted replacement was the blend slot.
        if(clip is null)return;
        if(blend)
        {
            secondary=new(clip);
            blendDuration=.5f;
            if(clip.Rate>0 && clip.Frames/clip.Rate<blendDuration)blendDuration=clip.Frames/clip.Rate;
            blendRemaining=blendDuration;
        }
        else {primary=new(clip);blendDuration=blendRemaining=0;}
    }
    public Pose? Advance(float delta)
    {
        AdvancedSingleProgress=primary?.Advance(delta);
        secondary?.Advance(delta);
        if(secondary is not null)AdvancedSingleProgress=null;
        if(primary is null)return Submitted;
        if(secondary is null)return Submitted=new(primary.Clip,primary.Frame,null,0,0);
        blendRemaining-=delta;
        float amount=1-Math.Clamp(blendRemaining/blendDuration,0,1);
        float weight=amount<.5f?(1-MathF.Cos(amount*MathF.PI))*.5f:
            (MathF.Sin((amount-.5f)*MathF.PI)+1)*.5f;
        Submitted=new(primary.Clip,primary.Frame,secondary.Clip,secondary.Frame,weight);
        // Native renders the two samples first and promotes only at < 0.
        if(blendRemaining<0)
        {
            primary=secondary;primary.Complete=false;secondary=null;
            blendDuration=blendRemaining=0;
        }
        return Submitted;
    }
}
