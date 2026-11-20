using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public sealed class EnbRetailDazzleHistoryState(uint creationClock=0)
{
    public uint CreationClock { get; }=creationClock;
    public EnbRetailDazzleHistory Value { get; set; }

    // 0096C02F..C084: unsigned age, native millisecond conversion and strict
    // phase > BlinkOnTime. The creation epoch belongs to the RenderObj clone.
    public bool IsBlinkOn(uint now,float period,float onTime,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        if(!(period>0))return true;
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        R seconds=R.MultiplyFloatMemory((R)(double)unchecked(now-CreationClock),0x3a83126f);
        double phase=(double)seconds%period;
        return !(phase>onTime);
    }
}

/// <summary>0096C70A..7EB: frame-clock decay and retained dazzle intensity/size.
/// Visibility reacquisition snaps immediately; only zero visibility blends.</summary>
public readonly record struct EnbRetailDazzleHistory(float Intensity,float Size)
{
    public EnbRetailDazzleHistory Advance(float intensity,float size,float visibility,float weight,uint elapsed,
        EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
    {
        if(visibility!=0&&!float.IsNaN(visibility))return new(intensity,size);
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        float decay=(float)Math.Pow(weight,Math.Max(1u,elapsed));
        R incoming=(R)1-(R)decay;
        R blended=(R)decay*(R)Intensity+incoming*(R)intensity;
        // FST precedes FCOMP: compare the retained register, not its float store.
        float nextIntensity=(double)blended>=BitConverter.UInt32BitsToSingle(0x3d4ccccd)?(float)blended:0;
        return new(nextIntensity,(float)((R)decay*(R)Size+incoming*(R)size));
    }
}
