using System.Numerics;

namespace EnbNext.Rendering;

public static partial class EnbCurrentRetailParticleEmitterRuntime
{

    public static bool TryComputeBufferBounds(IEnbParticleRenderOwner instance,
        IReadOnlyList<EnbCurrentRetailParticleBirth> births,uint randomSeedSalt,long clock,float scale,
        EnbRetailParticleMotionHistory motionHistory,float maximumSize,out EnbRetailParticleBufferBounds bounds,out string? diagnostic)
        =>TrySampleBufferCore(instance,births,randomSeedSalt,clock,scale,motionHistory,null,maximumSize,out bounds,out diagnostic);

    private static bool TrySampleBufferCore(IEnbParticleRenderOwner instance,
        IReadOnlyList<EnbCurrentRetailParticleBirth> births,uint randomSeedSalt,long clock,float scale,
        EnbRetailParticleMotionHistory motionHistory,List<Vector3>? result,float maximumSize,
        out EnbRetailParticleBufferBounds bounds,out string? diagnostic)
    {
        bounds=default;diagnostic=null;
        Vector3 minimum=default,maximum=default;
        int count=0;
        void Append(Vector3 position)
        {
            if(result is not null){result.Add(position);return;}
            if(count++==0){minimum=maximum=position;return;}
            minimum=Vector3.Min(minimum,position);maximum=Vector3.Max(maximum,position);
        }
        long lifetime=EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(instance.Definition.LifetimeSeconds);
        motionHistory.Expire(clock,lifetime);
        CompiledProperties? properties=null;
        for (int birthIndex = 0; birthIndex < births.Count; birthIndex++)
        {
            var birth = births[birthIndex];
            long age=clock-birth.Time;
            if(age<0||age>=lifetime)continue;
            //00961D10 scans the buffer's stored positions. Color, size,
            //rotation and UV evaluation belong to rendering, not this query.
            if (birth.NativeBufferPosition is {} storedPosition && IsFinite(storedPosition))
            { Append(storedPosition); continue; }
            if(birth is {SampledPosition:not null,SampledVelocityPerMillisecond:not null})
            {
                var motion=RetainedMotion(instance.Definition,birth,birth.Ordinal,age,scale,motionHistory);
                if(!IsFinite(motion.Position))
                {diagnostic=$"ParticleEmitter '{instance.Definition.Name}' produced non-finite particle position.";return false;}
                Append(motion.Position);continue;
            }
            var bornWorld=instance.Definition.LocalSpace?instance.WorldTransform:birth.World;
            if(!TryEvaluateParticle(instance,ref properties,bornWorld,randomSeedSalt,birth.Ordinal,age,scale,out var state,out diagnostic,
                birth.LocalVelocityPerSecond,birth,motionHistory))return false;
            var center=instance.Definition.LocalSpace?state.LocalCenter??state.Center:state.Center;
            if(!instance.Definition.LocalSpace && birth.NativeBirthRotation is null && birth.NativeBufferPosition is null && birth.SampledPosition is null)
                center+=birth.InheritedVelocityPerMillisecond*age;
            Append(center);
        }
        if(result is null && count!=0)bounds=EnbRetailParticleBufferBounds.FromExtrema(minimum,maximum,maximumSize);
        return true;
    }
}
