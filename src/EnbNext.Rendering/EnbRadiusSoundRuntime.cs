using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>
/// Replacement-client radius policy: explicit binary32 arithmetic, inclusive
/// distance, retained unary position, and the recovered wrapping deadline.
/// This does not claim emulation of arbitrary retail x87 control environments.
/// </summary>
public sealed class EnbRadiusSoundRuntime
{
    public EnbCurrentRetailRadiusSoundSchedule Schedule { get; }
    public float Radius { get; }
    public Vector3? Position { get; private set; }

    public EnbRadiusSoundRuntime(EnbEffectDefinition definition, uint initialDeadline)
    {
        if (!EnbRetailIniDocument.TryReadFloat(definition.Properties,"TriggerSFXRadius",out float radius) ||
            !float.IsFinite(radius) || radius < 0 || !float.IsFinite(radius * radius))
            throw new InvalidDataException("Invalid sound trigger radius");
        Radius=radius;
        Schedule=new(initialDeadline,Interval("TriggerSFXMinElapsedTime"),Interval("TriggerSFXMaxElapsedTime"));
        int Interval(string key)
        {
            float seconds=1;
            if (definition.Properties.ContainsKey(key) && !EnbRetailIniDocument.TryReadFloat(definition.Properties,key,out seconds))
                throw new InvalidDataException($"Invalid {key}");
            float milliseconds=seconds*1000;
            if (!float.IsFinite(milliseconds) || milliseconds<0 || (double)milliseconds>int.MaxValue)
                throw new InvalidDataException($"Invalid {key}");
            return (int)milliseconds;
        }
    }

    public bool Resolve(EnbCurrentRetailEffectSoundNode node,Func<uint,Vector3?> findPosition,
        Func<uint,string,Vector3?>? findBonePosition,out string? error,
        Func<EnbCurrentRetailEffectSoundNode,Vector3?>? findEffectPosition=null)
    {
        bool tracks=!node.Definition.Properties.ContainsKey("TrackSource") ||
            EnbRetailIniDocument.ReadBool(node.Definition.Properties,"TrackSource");
        error=null;
        if (Position is not null && !tracks) return true;
        if (EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(node,findPosition,out var position,out _,out error,
            findBonePosition,radiusWorldPosition:true,findEffectPosition:findEffectPosition)) { Position=position; return true; }
        // A previously bound unary effect retains its transform when its source
        // disappears. Initial binding never substitutes the listener or zero.
        if (Position is not null) { error=null; return true; }
        return false;
    }

    public bool TryAttempt(uint now,Vector3 camera,Action attempt,ref uint randomState)
        => Schedule.TryAttempt(now,Position is Vector3 p && IsInside(p,camera,Radius),attempt,ref randomState);

    public bool TryAttempt(uint now,Vector3 camera,Action attempt,Func<int,int,int> random)
        => Schedule.TryAttempt(now,Position is Vector3 p && IsInside(p,camera,Radius),attempt,random);

    public static bool IsInside(Vector3 position,Vector3 camera,float radius)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) ||
            !float.IsFinite(camera.X) || !float.IsFinite(camera.Y) || !float.IsFinite(camera.Z) ||
            !float.IsFinite(radius) || radius<0) return false;
        float x=position.X-camera.X, y=position.Y-camera.Y, z=position.Z-camera.Z;
        float xx=x*x, yy=y*y, zz=z*z;
        float yz=yy+zz;
        float distance=xx+yz;
        return distance<=radius*radius;
    }
}
