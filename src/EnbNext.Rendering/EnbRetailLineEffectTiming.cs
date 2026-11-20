using System.Numerics;
using EnbNext.Assets;
using R=EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

// Current retail 006e5040 / 006e5e00. Growth advances the leading
// endpoint; shrink advances the trailing endpoint (unless inverted).
public static class EnbRetailLineEffectTiming
{
    public static void Apply(EnbEffectDefinition definition, uint age, uint duration,
        ref Vector3 start, ref Vector3 end,uint actualStartTick=0)
    {
        //006E5E44 uses an unsigned absolute-clock comparison before subtracting.
        if(unchecked(actualStartTick+age)<actualStartTick)return;
        using var arithmetic=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        var p = definition.Properties;
        float Read(string key) => EnbRetailIniDocument.TryReadFloat(p,key,out var value) ? value : 0;
        bool Flag(string key) => p.TryGetValue(key,out var value) &&
            (value.Equals("TRUE",StringComparison.OrdinalIgnoreCase) || value == "1");
        uint Milliseconds(string key)
        {
            float seconds=Read(key);
            if(!float.IsFinite(seconds) || seconds>uint.MaxValue/1000d)
                throw new InvalidDataException("Invalid line timing");
            return (uint)((double)Math.Max(seconds,0)*1000d);
        }
        R Progress(uint elapsed,uint interval,string key)
        {
            float acceleration=Read(key);
            if(!float.IsFinite(acceleration)) throw new InvalidDataException("Invalid line acceleration");
            acceleration=Math.Clamp(acceleration,0,1);
            //006E5040 spills the reciprocal and both constructor coefficients.
            float reciprocal=(float)((R)1/interval);
            float quadratic=(float)((R)reciprocal*reciprocal*acceleration);
            float linear=(float)(((R)1-acceleration)*reciprocal);
            return ((R)elapsed*quadratic+linear)*elapsed;
        }
        //006E5E00 uses FSUB/FMUL/FADD for each component. Vector3.Lerp
        //may distribute the weights or fuse operations and changes the result.
        static Vector3 Interpolate(Vector3 origin,Vector3 destination,R progress)
            => new((float)(((R)destination.X-origin.X)*progress+origin.X),
                (float)(((R)destination.Y-origin.Y)*progress+origin.Y),
                (float)(((R)destination.Z-origin.Z)*progress+origin.Z));
        uint grow=Milliseconds("GrowTime"), shrink=Milliseconds("ShrinkTime");
        if(grow is >0 and <uint.MaxValue && age<grow)
        {
            R f=Progress(age,grow,"GrowAccelFactor");
            if(Flag("InvertGrow")) start=Interpolate(end,start,f);
            else end=Interpolate(start,end,f);
        }
        else if(duration!=uint.MaxValue && shrink>0 && shrink<duration && age>duration-shrink)
        {
            R f=Progress(age-(duration-shrink),shrink,"ShrinkAccelFactor");
            if(Flag("InvertShrink")) end=Interpolate(end,start,f);
            else start=Interpolate(start,end,f);
        }
    }
}
