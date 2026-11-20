using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<ulong,HashSet<CompositeOccurrence>> _attachmentParentOwners=[];
    private long _attachmentEpoch;
    private long _ordinaryAttachmentMutationPreparations,_generatedAttachmentReconciliations;
    private bool TryGetAttachmentParent(RetainedEmitterKey key,out LiveRootInstance parent)
    {
        if(_compositeOccurrences.TryGetValue(key,out var occurrence)&&occurrence.AttachmentEpoch==_attachmentEpoch&&
            occurrence.AttachmentParent is {} current){parent=current;return true;}
        parent=null!;return false;
    }
    private sealed class AttachmentComposite
    {
        public readonly EnbW3dRenderCompositeChild Descriptor;
        public readonly float Cost;
        public AttachmentComposite(EnbWarpPlumeAsset asset,float scale)
        {
            var resource=asset.Resource;
            var hlod=resource.HierarchicalLods.Single();
            string Name(int index)=>$"<attachment-emitter:{index}>";
            hlod=hlod with {Aggregate=new(0,asset.Particles.Select((p,i)=>new EnbW3dRenderHlodMember((uint)p.Pivot,Name(i))).ToArray())};
            resource=resource.Copy().SetHierarchicalLods([hlod]).SetCompositeRenderChildren(resource.CompositeRenderChildren.Concat(
                asset.Particles.Select((p,i)=>new EnbW3dRenderCompositeChild(Name(i),0x0f,default,0))).ToArray());
            var post=new EnbW3dHierarchyState(hlod.HierarchyName,Matrix4x4.Identity,scale,new Dictionary<int,Matrix4x4>());
            if(!EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,hlod,Matrix4x4.Identity,[],post,out var bounds,out var error) ||
                !EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(resource,hlod,_=>true,out var costs,out error) ||
                !EnbW3dHlodBudgetCompiler.TryComputeW3dAggregateFixedRenderCost(resource,hlod,_=>true,out var aggregateCost,out error))
                throw new InvalidDataException(error);
            //Store the already-scaled clone bounds: attachment occurs after
            //the ship prototype scale, which must not scale this clone again.
            Descriptor=new(hlod.Name,0x19,new(new(bounds.LocalSphere.Center,bounds.LocalSphere.Radius),bounds.LocalBox.Center,bounds.LocalBox.Extents),costs[0]);
            Cost=EnbW3dHlodBudgetCompiler.CurrentRetailLevelCost(costs[0])+aggregateCost;
        }
    }
    private void PrepareAttachmentComposites(RootTaskList roots)
        =>PrepareAttachmentCompositesForOwners(roots,null);

    private void PrepareAttachmentCompositesForOwners(RootTaskList roots,IReadOnlySet<ulong>? refreshedOwners,bool rootsAreSelected=false)
    {
        if(refreshedOwners is not null)
        {
            foreach(var owner in refreshedOwners)
                if(_attachmentParentOwners.TryGetValue(owner,out var occurrences))
                    foreach(var occurrence in occurrences){occurrence.AttachmentParent=null;occurrence.GeneratedAttachmentEpoch=-1;}
            if(!rootsAreSelected){var selected=new RootTaskList();foreach(var root in roots)if(refreshedOwners.Contains(root.Object.RenderIdentity))selected.Add(root);roots=selected;}
        }
        else _attachmentEpoch++;
        foreach(var root in roots)
        {
            var occurrence=ResolveCompositeOccurrence(root);
            occurrence.AttachmentEpoch=_attachmentEpoch;occurrence.AttachmentParent=root;
        }
        if(_shipExhaustOwner is not null)return; //BorrowRetainedChildMembership already copied the owner's bounds.
        var desired=new Dictionary<LiveRootInstance,Dictionary<string,(uint Pivot,EnbW3dRenderCompositeChild Child)>>();
        // Generated attachment producers retain their current compatibility
        // adapter until their construction/removal notifications are connected.
        // Ordinary hierarchy membership is driven only by mutation events.
        foreach(var (key,owner) in _warpPlumes)
            if(!owner.Particles.All(p=>p.State.Destroyed))Add(key.Parent,key.Pivot,$"<plume:{key}>",owner.Composite);
        foreach(var (key,owner) in _attachedEffects)
            if(!owner.Buffers.All(b=>b.Destroyed))Add(key.Parent,key.Pivot,$"<attach:{key}>",owner.Composite);
        foreach(var (parent,child,bone) in _bloodAttachments.Concat(_movieAttachments).Concat(_shipDamageAttachments))
        {
            if(refreshedOwners is not null && !refreshedOwners.Contains(parent.Object.RenderIdentity))continue;
            var retained=child.RetainedQueryRoot??child.Root;
            string name=$"<{child.InstancePath}>";
            if(!TryDescribeCompositeChild(retained,name,out var descriptor,out _))continue;
            ResolveCompositeOccurrence(parent).GeneratedAttachmentEpoch=_attachmentEpoch;
            if(!desired.TryGetValue(parent,out var members))desired.Add(parent,members=[]);
            members.Add(name,((uint)bone,descriptor));
        }
        foreach(var root in roots)
        {
            if(root.Root.Hlod is null)continue;
            var occurrence=ResolveCompositeOccurrence(root);
            bool generated=occurrence.GeneratedAttachmentEpoch==_attachmentEpoch;
            Dictionary<string,(uint Pivot,EnbW3dRenderCompositeChild Child)>? external=null;
            if(generated)generated=desired.TryGetValue(root,out external);
            if(!occurrence.AttachmentsDirty&&!generated&&!occurrence.HadGeneratedAttachments)continue;
            if(occurrence.AttachmentsDirty&&occurrence.Source is not null)_ordinaryAttachmentMutationPreparations++;
            if(generated||occurrence.HadGeneratedAttachments)_generatedAttachmentReconciliations++;
            var membership=occurrence.Membership;
            Dictionary<string,(uint Pivot,EnbW3dRenderCompositeChild Child)>? additions=null;
            if(root.Root.RslBoneNode is not null)
            {
                try
                {
                    var members=RslBoneCompositeMembers(root.Root);
                    if(members.Count!=0)additions=new(members);
                }
                catch(InvalidDataException error)
                {
                    _retainedEmitterErrors.Add($"RSL attachment bounds: {error.Message}");
                    if(membership is not null)additions=membership.Attachments.Where(pair=>pair.Key.StartsWith("<rsl-bone:",StringComparison.Ordinal)).ToDictionary();
                }
            }
            if(external is not null)
            {
                additions??=[];
                foreach(var pair in external)additions.Add(pair.Key,pair.Value);
            }
            occurrence.AttachmentsDirty=false;occurrence.HadGeneratedAttachments=generated;
            if(membership is null)
            {
                if(additions is null)continue;
                membership=CreateCompositeMembership(root);
            }
            if(membership.Attachments.Count==(additions?.Count??0) && membership.Attachments.All(pair=>additions!.TryGetValue(pair.Key,out var value)&&value==pair.Value))continue;
            membership.Attachments.Clear();
            if(additions is not null)foreach(var pair in additions)membership.Attachments.Add(pair.Key,pair.Value);
            InvalidateLegoSceneOwner(root.Object.RenderIdentity);
            membership.Rebuild();
            Propagate(root,membership);
        }
        foreach(var root in roots)
            if(CompositeMembership(root) is {} membership)root.RetainedQueryRoot=membership.QueryRoot;

        void Add(RetainedEmitterKey key,int pivot,string name,AttachmentComposite composite)
        {
            if(refreshedOwners is not null && !refreshedOwners.Contains(key.Owner))return;
            if(!TryGetAttachmentParent(key,out var parent)||parent.Root.Hlod is null)return;
            ResolveCompositeOccurrence(parent).GeneratedAttachmentEpoch=_attachmentEpoch;
            if(!desired.TryGetValue(parent,out var members))desired.Add(parent,members=[]);
            members.Add(name,((uint)pivot,composite.Descriptor));
        }
        void Propagate(LiveRootInstance child,RetainedCompositeMembership membership)
        {
            //98E4B0/98E0F0 ->98F1D0 -> parent+100. Preserve the separately
            //proven empty-child early return and cached ancestor bounds.
            if(child.Parent is not {} parent || child.ParentStorageOrdinal is not {} slot ||
                (parent.Root.Hlod is null && parent.Root.Collection is null))return;
            var ancestor=CreateCompositeMembership(parent);
            InvalidateLegoSceneOwner(parent.Object.RenderIdentity);
            ancestor.Replacements[slot]=membership.QueryRoot;
            if(membership.HasChildren){ancestor.Rebuild();Propagate(parent,ancestor);}
        }
    }
    private bool AttachmentParentDraws(RetainedEmitterKey key)
        =>TryGetAttachmentParent(key,out var parent) && parent.ShouldDraw && CollectionAncestorsDraw(parent);

    private float AttachmentCompositeCost()
    {
        var source=_shipExhaustOwner??this;float cost=0;
        foreach(var (key,owner) in source._warpPlumes)
            if(owner.Active && AttachmentParentDraws(key.Parent))cost+=owner.Composite.Cost;
        foreach(var (key,owner) in source._attachedEffects)
            if(owner.Buffers.Any(b=>!b.Destroyed && b.Started) && AttachmentParentDraws(key.Parent))cost+=owner.Composite.Cost;
        return cost;
    }
}
