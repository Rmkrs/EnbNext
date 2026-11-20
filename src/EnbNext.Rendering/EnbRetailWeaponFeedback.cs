using R = EnbNext.Rendering.EnbRetailExtendedX87Register;

namespace EnbNext.Rendering;

/// <summary>Presentation only: current retail 0068E360 / 006A4020. Never a fire gate.</summary>
public readonly record struct EnbRetailWeaponFeedback(int Frame, int Cue, float Angle, bool Active, bool Cooling)
{
    public bool Invalid { get; init; }
    public bool WrongTarget { get; init; }
    public bool NoAmmo { get; init; }
    public float RangeValue { get; init; }

    public static bool UsesTargetRange(uint type) => type is 1 or 5 or 14 or 15 or 16 or 17;
    public static bool UsesEffectRange(uint type) => type is 0 or 2 or 6 or 7 or 8 or 11 or 12;

    public static EnbRetailWeaponFeedback EvaluateSkill(float? distance, float range, int mode, float angle, uint elapsed, EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24, bool audibleTarget = true, int previousCue = 0)
        => EvaluateMode(0,distance,range,null,0,audibleTarget,angle,elapsed,mode,precision,previousCue);

    public static EnbRetailWeaponFeedback EvaluateEffect(uint state,float? distance,float? range,uint? usage,uint? targets,uint? validity,
        long? readyTime,long now,bool audibleTarget,float angle,uint elapsed,float previousRange = 0,int previousCue = 0)
    {
        // 0068E360: effect usage/target/validity fields, independent of weapon ItemState gating.
        uint targetFlags=targets.GetValueOrDefault();
        int mode=(usage.GetValueOrDefault()&5)!=0 && (validity.GetValueOrDefault(0x100)&0x3f8)==0 && (targetFlags&0x34)!=0?2:0;
        bool usable=(usage.GetValueOrDefault()&5)!=0;
        float retainedRange=usable && (targetFlags&0x44)!=0 ? range.GetValueOrDefault() : previousRange;
        var feedback=EvaluateMode(state,distance,retainedRange,readyTime,now,audibleTarget,angle,elapsed,mode,previousCue:previousCue);
        return feedback with {RangeValue=retainedRange,NoAmmo=(state&8)!=0,
            Invalid=!usable || validity.GetValueOrDefault(0x100)!=0 || feedback.Cooling,
            WrongTarget=usable && (validity.GetValueOrDefault(0x100)&0x70)!=0};
    }

    public static EnbRetailWeaponFeedback Evaluate(uint state, float? distance, float? range,
        long? readyTime, long now, bool audibleTarget, float angle, uint elapsed, int previousCue = 0)
    {
        int mode=(state&0x36)!=0?0:2-((state&0x800)!=0?1:0);
        return EvaluateMode(state,distance,range,readyTime,now,audibleTarget,angle,elapsed,mode,previousCue:previousCue)
            with {RangeValue=range.GetValueOrDefault(),NoAmmo=(state&8)!=0,
                Invalid=(state&0x680)!=0,WrongTarget=(state&2)!=0};
    }

    private static EnbRetailWeaponFeedback EvaluateMode(uint state,float? distance,float? range,long? readyTime,long now,bool audibleTarget,float angle,uint elapsed,int mode,EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24, int previousCue = 0)
    {
        bool active=(state&1)!=0, cooling=readyTime is >0 && readyTime>unchecked((uint)now);
        if(mode==0 || distance is not float d || !float.IsFinite(d) || d<0 ||
            range is not float r || !float.IsFinite(r) || r<=0)
            return new(-1,0,angle,active,cooling);
        // 6A4020: retain x87 intermediates and the explicit float spills.
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        ushort control=precision==EnbRetailLinePrecision.Single24?(ushort)0x7f:(ushort)0x27f;
        if(d>(float)((R)r+(R)r))return new(0,0,angle,active,cooling);
        if(d>r)
        {
            R frame=((R)1-((R)d-(R)r)/(R)r)*(R)15;
            return new(unchecked((int)R.TruncateInteger64(frame)),audibleTarget?1:previousCue,
                angle>0.0001f?(float)((R)angle*(R).8f):0,active,cooling);
        }
        if(mode==1)return new(14,audibleTarget?1:previousCue,angle,active,cooling);
        R step=R.FromInteger64(elapsed,control)*(R).002f;
        R next=((R)1-(R)d/(R)r)*step+(R)angle;
        angle=(float)next;
        if((R.CompareFloatMemory(next,0x3fc90fdb)&0x4100)==0)
        {
            R remainder=angle;ushort status;
            do {remainder=R.PartialRemainder(remainder,(R)1.5707963705062866,control,out status);}
            while((status&0x400)!=0);
            angle=(float)remainder;
        }
        return new(15,audibleTarget?2:previousCue,angle,active,cooling);
    }
}
