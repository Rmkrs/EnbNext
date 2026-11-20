using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<EffectHlodKey,EffectSequenceState> _effectSequences=[];
    private sealed class EffectSequenceState(EnbCurrentRetailRslAnimationObservations observations)
    {
        public EnbCurrentRetailParticlePoseHistory Poses {get;}=new();
        public EnbCurrentRetailRslAnimationObservations Observations {get;}=observations;
        public EnbCurrentRetailRslAnimationSequenceRuntime.Sample Current {get;private set;}
        public void Record(uint age,Matrix4x4 world)
        {Poses.Record(age,world);Current=Observations.Record(age);}
    }
    private EffectSequenceState? PrepareEffectSequence(EffectHlodKey key,EnbCurrentRetailRObjEffectAsset asset,
        uint age,Matrix4x4 world)
    {
        if(asset.Sequence is null)return null;
        if(!_effectSequences.TryGetValue(key,out var state))
        {
            state=new(asset.Sequence.CreateObservations());
            if(_effectObjectInstances.TryGetValue((key.Effect,key.Generation,key.Sequence),out var pending) &&
                pending.OwnerIdentity==key.Owner)
                foreach(var pose in pending.Poses.Samples)
                    state.Record(pose.Age,Matrix4x4.CreateFromQuaternion(pose.Rotation)*Matrix4x4.CreateTranslation(pose.Position));
            _effectSequences.Add(key,state);
        }
        state.Record(age,world);
        return state;
    }
}
