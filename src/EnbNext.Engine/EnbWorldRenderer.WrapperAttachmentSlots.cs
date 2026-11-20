using EnbNext.Rendering;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private sealed class RslWrapperOwner(bool preserveWhileResident=false)
    {
        public EnbRslObjectState? CurrentState;
        public bool CallbackAttached,PreviousAttached,Queued,Retired;
        public int TaskCount;
        public readonly bool PreserveWhileResident=preserveWhileResident;
        public bool Commit()
        {
            bool attached=!Retired&&(CallbackAttached||TaskCount!=0||(PreserveWhileResident&&PreviousAttached));
            bool changed=PreviousAttached!=attached;PreviousAttached=attached;Queued=false;return changed;
        }
    }
    private readonly List<RslWrapperOwner> _wrapperAttachmentChanges=[];
    private long _wrapperAttachmentTransitions;
    private void QueueWrapperAttachment(RslWrapperOwner slot)
    {
        if(slot.Queued)return;
        slot.Queued=true;_wrapperAttachmentChanges.Add(slot);
    }
    private void SetOwnedWrapperAttached(SceneOwner owner,bool attached)
    {
        var slot=owner.Attachment;
        if(slot.CallbackAttached==attached)return;
        slot.CallbackAttached=attached;QueueWrapperAttachment(slot);
    }
    private void SetEffectWrapperAttached(EffectRslOwner owner,bool attached)
    {
        var state=owner.Attachment;
        if(state.CallbackAttached==attached)return;
        state.CallbackAttached=attached;QueueWrapperAttachment(state);
    }
    // Commit at the same point as the former wrapper set publication. Tasks
    // contribute current invocation visibility; callback membership is retained.
    private void CommitWrapperAttachments()
    {
        foreach(var slot in _wrapperAttachmentChanges)
        {
            if(slot.Commit())_wrapperAttachmentTransitions++;
        }
        _wrapperAttachmentChanges.Clear();
    }
    private void ClearRetainedWrapperAttachmentHistory()
    {
        _retainedEmitterWrappersSeen.Clear();
        foreach(var slot in _wrapperAttachmentChanges)slot.Queued=false;
        _wrapperAttachmentChanges.Clear();
        foreach(var effect in _effectRslOwners.Values)
        {var state=effect.Attachment;state.PreviousAttached=false;if(state.CallbackAttached||state.TaskCount!=0)QueueWrapperAttachment(state);}
        if(_ownedScene is not null)foreach(var node in _ownedScene.ActiveNodes)
            {var slot=node.State.Attachment;slot.PreviousAttached=false;if(slot.CallbackAttached||slot.TaskCount!=0)QueueWrapperAttachment(slot);}
    }
}
