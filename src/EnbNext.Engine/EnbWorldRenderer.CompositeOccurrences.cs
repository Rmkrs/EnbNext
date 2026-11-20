using EnbNext.Presentation;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    // The occurrence owns membership; the index is only for construction and
    // explicit external queries. A negative binding is retained as well.
    private sealed class CompositeOccurrence(EnbWorldRenderer view,RetainedEmitterKey key,bool rsl)
    {
        public readonly EnbWorldRenderer View=view;
        public readonly RetainedEmitterKey Key=key;
        public readonly bool Rsl=rsl;
        public SceneOwner? Source;
        public RetainedCompositeMembership? Membership;
        public bool Valid=true;
        public CompositeFrameSlot FrameSlot=null!;
        public long AttachmentEpoch;
        public LiveRootInstance? AttachmentParent;
        public LiveRslBoneNode? AttachmentNode;
        public bool AttachmentsDirty=true;
        public bool DamageAttachmentsDirty=true;
        public bool HadGeneratedAttachments;
        public long GeneratedAttachmentEpoch=-1;
        public void InvalidateAttachments(){AttachmentsDirty=true;DamageAttachmentsDirty=true;}
        public void MembershipChanged()
        {
            View._dirtyCompositeOccurrences.Add(this);
            if(Source?.RslCallbackTree is {Persistent:true} tree)tree.PersistentValid=false;
        }
    }
    private sealed class CompositeFrameSlot
    {
        public int References;
        public long Epoch;
        public LiveRootInstance? Current;
    }
    private long _compositeFrameEpoch;
    private readonly HashSet<CompositeOccurrence> _dirtyCompositeOccurrences=[];
    private readonly Dictionary<RetainedEmitterKey,CompositeOccurrence> _compositeOccurrences=[];
    private readonly HashSet<CompositeOccurrence> _compatCompositeOccurrences=[];
    private long _ordinaryCompositeBindings;
    private long _compositeOccurrenceResolutions;
    private long _compositeFrameIndexWrites;
    private long _compositePropagationVisits;
    private CompositeOccurrence ResolveCompositeOccurrence(LiveRootInstance instance)
    {
        if(instance.PreparedCompositeOccurrence is {Valid:true} current&&ReferenceEquals(current.View,this))
        {AttachCompositeSource(current,instance);return current;}
        _compositeOccurrenceResolutions++;
        var key=CompositeMembershipKey(instance);
        if(!_compositeOccurrences.TryGetValue(key,out var occurrence))
        {
            occurrence=new(this,key,instance.IsRsl);
            if(instance.Root.RslBoneNode is {} attachmentNode)
            {
                occurrence.AttachmentNode=attachmentNode;
                attachmentNode.Runtime.DefinitionChanged+=occurrence.InvalidateAttachments;
                attachmentNode.ExportChanged+=occurrence.InvalidateAttachments;
            }
            var frameKey=(key.Owner,key.Generation,key.Path,key.PhysicalFactory,key.BoneFactory);
            if(!_retainedCompositeFrameRoots.TryGetValue(frameKey,out var slot))
                {_retainedCompositeFrameRoots.Add(frameKey,slot=new());_compositeFrameIndexWrites++;}
            slot.References++;occurrence.FrameSlot=slot;
            _retainedCompositeMembership.TryGetValue(key,out occurrence.Membership);
            if(occurrence.Membership is {} membership)membership.Changed=occurrence.MembershipChanged;
            _compositeOccurrences.Add(key,occurrence);
            if(!_attachmentParentOwners.TryGetValue(key.Owner,out var ownerOccurrences))
                _attachmentParentOwners.Add(key.Owner,ownerOccurrences=[]);
            ownerOccurrences.Add(occurrence);
            _compatCompositeOccurrences.Add(occurrence);
        }
        AttachCompositeSource(occurrence,instance);
        return instance.PreparedCompositeOccurrence=occurrence;
    }
    private void AttachCompositeSource(CompositeOccurrence occurrence,LiveRootInstance instance)
    {
        if(occurrence.Source is null&&_shipExhaustOwner is null&&_ownedSceneFrame&&
            instance.PreparedOwner?.SceneSource is {} source&&source.Definition.RenderIdentity==occurrence.Key.Owner)
        {
            occurrence.Source=source;source.CompositeOccurrences.Add(occurrence);
            _compatCompositeOccurrences.Remove(occurrence);
            if(occurrence.Key.PhysicalFactory is PhysicalFactoryLifetime factory)factory.CompositeOccurrences.Add(occurrence);
            _ordinaryCompositeBindings++;
        }
    }
    private RetainedCompositeMembership? CompositeMembership(LiveRootInstance instance)
        =>(_shipExhaustOwner??this).ResolveCompositeOccurrence(instance).Membership;
    private RetainedCompositeMembership CreateCompositeMembership(LiveRootInstance instance)
    {
        var occurrence=ResolveCompositeOccurrence(instance);
        if(occurrence.Membership is {} existing)return existing;
        var membership=new RetainedCompositeMembership(instance.Root);
        membership.Changed=occurrence.MembershipChanged;
        _dirtyCompositeOccurrences.Add(occurrence);
        _retainedCompositeMembership.Add(occurrence.Key,membership);
        return occurrence.Membership=membership;
    }
    private void RetireCompositeOccurrence(CompositeOccurrence occurrence)
    {
        if(!occurrence.Valid)return;
        occurrence.Valid=false;
        if(occurrence.AttachmentNode is {} attachmentNode)
        {
            attachmentNode.Runtime.DefinitionChanged-=occurrence.InvalidateAttachments;
            attachmentNode.ExportChanged-=occurrence.InvalidateAttachments;
            occurrence.AttachmentNode=null;
        }
        _dirtyCompositeOccurrences.Remove(occurrence);
        if(--occurrence.FrameSlot.References==0)
        {
            _retainedCompositeFrameRoots.Remove((occurrence.Key.Owner,occurrence.Key.Generation,occurrence.Key.Path,occurrence.Key.PhysicalFactory,occurrence.Key.BoneFactory));_compositeFrameIndexWrites++;
            occurrence.FrameSlot.Current=null;
        }
        _compositeOccurrences.Remove(occurrence.Key);
        if(_attachmentParentOwners.TryGetValue(occurrence.Key.Owner,out var ownerOccurrences))
        {
            ownerOccurrences.Remove(occurrence);
            if(ownerOccurrences.Count==0)_attachmentParentOwners.Remove(occurrence.Key.Owner);
        }
        occurrence.AttachmentParent=null;
        _retainedCompositeMembership.Remove(occurrence.Key);
        _compatCompositeOccurrences.Remove(occurrence);
        occurrence.Source?.CompositeOccurrences.Remove(occurrence);
        if(occurrence.Key.PhysicalFactory is PhysicalFactoryLifetime factory)factory.CompositeOccurrences.Remove(occurrence);
        occurrence.Source=null;
        // In-flight consumers may still reference this membership. Retirement
        // removes its ownership/index links, not the queued value itself.
    }
    private void RetireOwnedCompositeOccurrences(SceneOwner source,IEnbRenderObject? next=null)
    {
        for(int i=source.CompositeOccurrences.Count-1;i>=0;i--)
        {
            var occurrence=source.CompositeOccurrences[i];
            if(next is null || occurrence.Key.PhysicalFactory is null && occurrence.Key.Generation!=
                (occurrence.Rsl?next.RenderObjectGeneration:next.VisualGeneration))RetireCompositeOccurrence(occurrence);
        }
    }
    private void ClearCompositeMembership()
    {
        foreach(var occurrence in _compositeOccurrences.Values.ToArray())RetireCompositeOccurrence(occurrence);
        _retainedCompositeMembership.Clear();
    }
}
