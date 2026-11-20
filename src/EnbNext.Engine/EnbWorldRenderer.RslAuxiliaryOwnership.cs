using EnbNext.Presentation;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    // Registration occurs on a new completion/transition, never in the frame walk.
    // Ordinary lifetime follows the source generation; only compatibility entries
    // participate in the legacy admission sweep.
    private sealed class AuxiliaryOwnership<TKey> where TKey:notnull
    {
        private readonly Dictionary<TKey,SceneOwner> sources=[];
        private readonly Dictionary<SceneOwner,HashSet<TKey>> owned=[];
        public readonly HashSet<TKey> Compatibility=[];
        public void Register(TKey key,SceneOwner? source)
        {
            Forget(key);
            if(source is null){Compatibility.Add(key);return;}
            sources.Add(key,source);
            if(!owned.TryGetValue(source,out var keys))owned.Add(source,keys=[]);
            keys.Add(key);
        }
        public void Forget(TKey key)
        {
            Compatibility.Remove(key);
            if(!sources.Remove(key,out var source))return;
            var keys=owned[source];keys.Remove(key);
            if(keys.Count==0)owned.Remove(source);
        }
        public void Retire(SceneOwner source,Action<TKey> remove)
        {
            if(!owned.Remove(source,out var keys))return;
            foreach(var key in keys){sources.Remove(key);remove(key);}
        }
        public void Clear(){sources.Clear();owned.Clear();Compatibility.Clear();}
    }
    private readonly AuxiliaryOwnership<LiveRslSoundInstanceKey> _soundCompletionLifetime=new();
    private readonly AuxiliaryOwnership<LiveRslAutomaticNextTransitionKey> _automaticTransitionLifetime=new();
    private readonly AuxiliaryOwnership<LiveRslPacketNextOnceTransitionKey> _packetTransitionLifetime=new();
    private SceneOwner? AuxiliarySource((ulong Identity,ulong Generation) key)
    {
        if(!_ownedSceneFrame)return null;
        if(_buildingSceneOwner is {} current && current.Definition.RenderIdentity==key.Identity &&
            current.Definition.RenderObjectGeneration==key.Generation)return current;
        return _ownedScene!.TryFind(key.Identity,out var node)&&node.Definition.RenderObjectGeneration==key.Generation?node.State:null;
    }
    private void RetireOwnedRslAuxiliaries(SceneOwner source)
    {
        _soundCompletionLifetime.Retire(source,key=>_rslSoundCompletions.Remove(key));
        _automaticTransitionLifetime.Retire(source,key=>_rslAutomaticNextTransitions.Remove(key));
        _packetTransitionLifetime.Retire(source,key=>_rslPacketNextOnceTransitions.Remove(key));
    }
    private void ClearRslAuxiliaryOwnership()
    {_soundCompletionLifetime.Clear();_automaticTransitionLifetime.Clear();_packetTransitionLifetime.Clear();}
    private void RemoveSoundCompletion(LiveRslSoundInstanceKey key)
    {_soundCompletionLifetime.Forget(key);_rslSoundCompletions.Remove(key);}
    private void RemoveAutomaticTransition(LiveRslAutomaticNextTransitionKey key)
    {_automaticTransitionLifetime.Forget(key);_rslAutomaticNextTransitions.Remove(key);}
    private void RemovePacketTransition(LiveRslPacketNextOnceTransitionKey key)
    {_packetTransitionLifetime.Forget(key);_rslPacketNextOnceTransitions.Remove(key);}
}
