using System.Numerics;

namespace EnbNext.Rendering;

// Owned by the retained emitter/buffer, so multiple render views share one
// update.0095D8E0's clock comparison suppresses repeated same-clock updates.
public sealed class EnbRetailParticleMotionHistory
{
    private readonly Dictionary<(long Birth,long Ordinal),Motion> _motions=[];

    internal bool TryGetState(long birth,long ordinal,long age,out (Vector3 Position,Vector3 Velocity) state)
    {
        if(_motions.TryGetValue((birth,ordinal),out var motion) && motion.Age==age)
        {state=(motion.Position,motion.Velocity);return true;}
        state=default;return false;
    }

    public Vector3 Sample(long birth,long ordinal,long age,Vector3 initialPosition,Vector3 initialVelocity,Vector3 acceleration)
        => SampleState(birth,ordinal,age,initialPosition,initialVelocity,acceleration).Position;

    public (Vector3 Position,Vector3 Velocity) SampleState(long birth,long ordinal,long age,Vector3 initialPosition,Vector3 initialVelocity,Vector3 acceleration)
    {
        var key=(birth,ordinal);
        if(!_motions.TryGetValue(key,out var motion))
        {
            var state=EnbRetailParticleKinematics.Initialize(initialPosition,initialVelocity,acceleration,age);
            motion=new(age,state.Position,state.Velocity);
        }
        else if(age!=motion.Age)
        {
            if(age<motion.Age)throw new InvalidDataException("Particle buffer render clock moved backwards");
            var state=EnbRetailParticleKinematics.Advance(motion.Position,motion.Velocity,acceleration,age-motion.Age);
            motion=new(age,state.Position,state.Velocity);
        }
        _motions[key]=motion;
        return (motion.Position,motion.Velocity);
    }

    public void Expire(long time,long lifetime)
    {
        // Dictionary removal preserves enumeration on this runtime; no snapshot
        // of the live position table is needed just to retire old entries.
        foreach(var key in _motions.Keys)
            if(time-key.Birth>=lifetime)_motions.Remove(key);
    }

    private readonly record struct Motion(long Age,Vector3 Position,Vector3 Velocity);
}
