using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Current retail 006EB150 / 006F0B10 / 006F0D40.</summary>
public static class EnbRetailSpiralSeekEffectPath
{
    public static Matrix4x4 Sample(uint duration, uint age, float radius, float spinRate,
        Vector3 source, Vector3 target)
    {
        if(!float.IsFinite(radius) || !float.IsFinite(spinRate))
            throw new InvalidDataException("Non-finite spiral parameters");
        // Constructor normalizes these two exceptional durations before storing
        // the reciprocal. Active lifetime/expiry remains owned by the scheduler.
        uint effectiveDuration=duration is 0 or uint.MaxValue?1:duration;
        float t=(float)(age*(double)(1f/effectiveDuration));
        float rate=spinRate*BitConverter.Int32BitsToSingle(0x3bcde32e);
        double turns=effectiveDuration*(double)rate, angle=turns*t;
        float sine=(float)Math.Sin(angle);
        double cosine=Math.Cos(angle);
        double amplitude=(t-(double)t*t)*radius*4;
        float derivative=(float)((1-2*(double)t)*radius*4);
        float y=(float)(amplitude*sine),z=(float)(amplitude*cosine);
        float dy=(float)(derivative*(double)sine+amplitude*cosine*turns);
        float dz=(float)(derivative*cosine-y*turns);
        float length=Vector3.Distance(source,target);
        var basis=EnbRetailSeekEffectPath.LookAlong(target-source,source);
        var position=Vector3.Transform(new Vector3(length*t,y,z),basis);
        var tangent=Vector3.TransformNormal(new Vector3(length,dy,dz),basis);
        return EnbRetailSeekEffectPath.LookAlong(tangent,position);
    }
}
