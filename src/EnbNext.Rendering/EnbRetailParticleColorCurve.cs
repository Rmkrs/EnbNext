using System.Numerics;
using EnbNext.Assets;
using Register = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public static class EnbRetailParticleColorCurve
{
    internal sealed class Compiled
    {
        private readonly (long Time,Vector3 Color,Vector3 Slope,bool Terminal)[] entries;
        private readonly EnbRetailLinePrecision precision;
        internal Compiled(IReadOnlyList<EnbCurrentRetailParticleColorKey> keys,long lifetimeMilliseconds,
            EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
        {
            this.precision=precision;
            using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
            int last=keys.Count-1;
            while(last>0&&Milliseconds(keys[last],precision)>=lifetimeMilliseconds)last--;
            entries=new (long,Vector3,Vector3,bool)[last+1];
            for(int i=0;i<=last;i++)
            {
                long start=Milliseconds(keys[i],precision);var color=Color(keys[i]);
                bool terminal=i==keys.Count-1;Vector3 slope=default;
                if(!terminal)
                {
                    Register end=i==last?(Register)keys[i+1].TimeSeconds*1000d:(double)Milliseconds(keys[i+1],precision);
                    Register reciprocal=(Register)1d/(end-(double)start);var next=Color(keys[i+1]);
                    slope=new((float)(((Register)next.X-color.X)*reciprocal),
                        (float)((Register)(float)((Register)next.Y-color.Y)*(float)reciprocal),
                        (float)((Register)(float)((Register)next.Z-color.Z)*(float)reciprocal));
                }
                entries[i]=(start,color,slope,terminal);
            }
        }
        internal Vector3 Sample(long ageMilliseconds,Vector3 randomOffset=default)
        {
            int index=entries.Length-1;
            while(index>0&&ageMilliseconds<entries[index].Time)index--;
            var entry=entries[index];var color=entry.Color;var slope=entry.Slope;
            if(entry.Terminal && float.IsFinite(randomOffset.X)&&float.IsFinite(randomOffset.Y)&&float.IsFinite(randomOffset.Z))
                return randomOffset+color;
            using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
            if(entry.Terminal)return new((float)((Register)randomOffset.X+color.X),
                (float)((Register)randomOffset.Y+color.Y),(float)((Register)randomOffset.Z+color.Z));
            float elapsed=ageMilliseconds-entry.Time;
            float Channel(float value,float gradient,float random)=>(float)((Register)(float)((Register)(float)((Register)gradient*elapsed)+value)+random);
            return new(Channel(color.X,slope.X,randomOffset.X),Channel(color.Y,slope.Y,randomOffset.Y),
                (float)((Register)slope.Z*elapsed+color.Z+randomOffset.Z));
        }
    }
    // Decoded keys are nondecreasing and include the initial value at zero.
    // 0095EF40 builds integer-ms keys; 00960F80 scans backward with unsigned
    // age >= key. Earlier keys at the same millisecond are never selected.
    public static Vector3 Sample(IReadOnlyList<EnbCurrentRetailParticleColorKey> keys,
        long ageMilliseconds, long lifetimeMilliseconds,EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24,
        Vector3 randomOffset=default)
    {
        using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
        int last = keys.Count - 1;
        while (last > 0 && Milliseconds(keys[last],precision) >= lifetimeMilliseconds) last--;
        int index = last;
        while (index > 0 && ageMilliseconds < Milliseconds(keys[index],precision)) index--;
        Vector3 color = Color(keys[index]);
        if (index == keys.Count - 1) return new((float)((Register)randomOffset.X+color.X),
            (float)((Register)randomOffset.Y+color.Y),(float)((Register)randomOffset.Z+color.Z));
        long start = Milliseconds(keys[index],precision);
        // 0095F335: the first key excluded by lifetime still supplies the
        // terminal slope, using its untruncated authored time.
        Register end = index == last
            ? (Register)keys[index + 1].TimeSeconds * 1000d
            : (double)Milliseconds(keys[index + 1],precision);
        Register reciprocal = (Register)1d / (end - (double)start);
        Vector3 next = Color(keys[index + 1]);
        float elapsed = ageMilliseconds - start;
        var slope=new Vector3((float)(((Register)next.X-color.X)*reciprocal),
            (float)((Register)(float)((Register)next.Y-color.Y)*(float)reciprocal),
            (float)((Register)(float)((Register)next.Z-color.Z)*(float)reciprocal));
        float Channel(float value,float gradient,float random)=>(float)((Register)(float)((Register)(float)((Register)gradient*elapsed)+value)+random);
        return new(Channel(color.X,slope.X,randomOffset.X),Channel(color.Y,slope.Y,randomOffset.Y),
            (float)((Register)slope.Z*elapsed+color.Z+randomOffset.Z));
    }

    private static long Milliseconds(EnbCurrentRetailParticleColorKey key,EnbRetailLinePrecision precision)
        => EnbRetailParticleEmissionArithmetic.Milliseconds(key.TimeSeconds,precision);

    private static Vector3 Color(EnbCurrentRetailParticleColorKey key)
        => key.NormalizedRgb;
}
