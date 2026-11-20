using EnbNext.Assets;
using Register = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

public static class EnbRetailParticleScalarCurve
{
    internal sealed class Compiled
    {
        private readonly (long Time,float Value,float Slope,bool Terminal)[] entries;
        private readonly EnbRetailLinePrecision precision;
        internal Compiled(IReadOnlyList<EnbCurrentRetailParticleScalarKey> keys,long lifetimeMilliseconds,
            EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24)
        {
            this.precision=precision;
            using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
            int last=keys.Count-1;
            while(last>0&&Milliseconds(keys[last],precision)>=lifetimeMilliseconds)last--;
            entries=new (long,float,float,bool)[last+1];
            for(int i=0;i<=last;i++)
            {
                long start=Milliseconds(keys[i],precision);
                bool terminal=i==keys.Count-1;
                Register end=terminal?0:i==last?(Register)keys[i+1].TimeSeconds*1000d:(double)Milliseconds(keys[i+1],precision);
                float slope=terminal?0:(float)(((Register)keys[i+1].Value-keys[i].Value)/(end-(double)start));
                entries[i]=(start,keys[i].Value,slope,terminal);
            }
        }
        internal float Sample(long ageMilliseconds,float storageScale=1,float randomOffset=0)
        {
            int index=entries.Length-1;
            while(index>0&&ageMilliseconds<entries[index].Time)index--;
            var entry=entries[index];
            // A terminal, unscaled curve adds two stored binary32 values.
            // Their sum rounds identically at the final binary32 store.
            if(entry.Terminal && storageScale==1 && float.IsFinite(entry.Value)&&float.IsFinite(randomOffset))
                return randomOffset+entry.Value;
            using var arithmetic=new EnbRetailLineArithmetic.Scope(precision);
            float value=(float)((Register)entry.Value*storageScale),random=(float)((Register)randomOffset*storageScale);
            if(entry.Terminal)return (float)((Register)random+value);
            float slope=(float)((Register)entry.Slope*storageScale);
            return (float)((Register)slope*(double)(ageMilliseconds-entry.Time)+random+value);
        }
    }

    private static long Milliseconds(EnbCurrentRetailParticleScalarKey key,EnbRetailLinePrecision precision)
        => EnbRetailParticleEmissionArithmetic.Milliseconds(key.TimeSeconds,precision);
}
