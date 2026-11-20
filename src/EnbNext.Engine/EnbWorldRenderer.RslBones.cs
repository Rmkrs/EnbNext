using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<(ulong RenderIdentity,ulong VisualGeneration),LiveRslBoneOwner> _rslBoneOwners=[];
    private readonly HashSet<(ulong RenderIdentity,ulong VisualGeneration)> _compatRslBoneOwners=[];
    private void DetachOwnedRslBones(SceneOwner owner)
    {
        _rslBoneOwners.Remove((owner.Definition.RenderIdentity,owner.Definition.RenderObjectGeneration));
        owner.RslBones=null;owner.RslBonesResolved=false;
    }
    private void ClearRslBoneOwners()
    {
        _rslBoneOwners.Clear();_compatRslBoneOwners.Clear();
        if(_ownedScene is not null)foreach(var node in _ownedScene.ActiveNodes)
            {node.State.RslBones=null;node.State.RslBonesResolved=false;}
    }
    private static readonly bool RetainRslDefinitions=Environment.GetEnvironmentVariable("ENB_LEGACY_RSL_DEFINITION_EXPORT")!="1";

    private void ConsumeRslBoneActions(IEnbRenderObject obj,EnbResolvedRslReference rsl,
        EnbRetailAssetBundle assets,EnbRslObjectState update,SceneOwner? sceneOwner=null)
    {
        var graph=rsl.ResourceGraph;
        if(graph is null)return;
        var owner=sceneOwner?.RslBones;
        if(sceneOwner is not {RslBonesResolved:true})
        {
            bool needed=InitialBaseLodBias(obj)!=1 || graph.States.Any(s=>s.Definition.Type==EnbRslStateType.Bone);
            if(sceneOwner is not null)sceneOwner.RslBonesResolved=true;
            if(!needed)return;
            var key=(obj.RenderIdentity,obj.RenderObjectGeneration);
            if(!_rslBoneOwners.TryGetValue(key,out owner))
            {
                owner=new LiveRslBoneOwner();
                _rslBoneOwners.Add(key,owner);
                if(_ownedScene is null||!_ownedScene.TryFind(obj.RenderIdentity,out var node)||
                    node.Definition.RenderObjectGeneration!=obj.RenderObjectGeneration)_compatRslBoneOwners.Add(key);
            }
            if(sceneOwner is not null)sceneOwner.RslBones=owner;
        }
        if(owner is null)return;

        ConsumeRslBoneActionsCore(obj,graph,assets,update,owner);
    }

    private void ConsumeRslBoneActionsCore(IEnbRenderObject obj,EnbResolvedRslLibraryResourceGraph graph,
        EnbRetailAssetBundle assets,EnbRslObjectState update,LiveRslBoneOwner owner,int? actionLimit=null,int? selectedObject=null)
    {

        LiveRslBoneNode? Resolve(int id)
        {
            if(owner.Nodes.TryGetValue(id,out var cached))return cached;
            if(!graph.TryFindState(id,out var state) ||
                state.Definition.Type is not (EnbRslStateType.Object or EnbRslStateType.Bone))return null;
            if(!TryMaterializeRslObjectRoot(obj.BaseAsset,id,state,assets,out var root,out var diagnostic))
            {owner.Diagnostic=diagnostic;return null;}
            try
            {
                root=ResolveEffectRslRoot(obj,id,root);
                var node=BuildRslBoneNode(root);
                node.StateId=id;
                owner.AddNode(id,node);
                return node;
            }
            catch(NotSupportedException ex){owner.Diagnostic=ex.Message;return null;}
        }

        bool ApplyCreationBias()
        {
            if(owner.CreationBiasApplied || owner.ActionCursor!=update.FactoryActionCount)return true;
            if(InitialBaseLodBias(obj)!=1 && update.FactoryObjectStateId is int id)
            {
                var node=Resolve(id);
                if(node is null)return false;
                node.Runtime.SetLodBias(InitialBaseLodBias(obj));
            }
            owner.CreationBiasApplied=true;
            return true;
        }

        if(owner.CurrentStateId is int starting && owner.Current is null)
        {
            owner.Current=Resolve(starting);
            if(owner.Current is null)return;
            owner.Current.Runtime.SetParent(owner.Anchor);
            owner.Current.Runtime.SetInheritedProperty(owner,true);
        }
        for(;owner.ActionCursor<(actionLimit??update.Actions.Count);owner.ActionCursor++)
        {
            if(!ApplyCreationBias())return;
            var action=update.Actions[owner.ActionCursor];
            if(!graph.TryFindState(action.StateId,out var state) ||
                state.Definition.Type is not (EnbRslStateType.Object or EnbRslStateType.Bone))continue;
            bool activate=action.Opcode==0x0030;
            if(!activate && action.Opcode!=0x0032)continue;
            //An enabled non-current selection does not call its getter. Bone
            //never becomes the current Object, so it has no unhide shortcut.
            bool virtualActivation=update.ActivationVirtualActionOrdinals.Contains(owner.ActionCursor);
            if(activate && !virtualActivation && state.Definition.Type==EnbRslStateType.Bone)continue;
            var node=Resolve(action.StateId);
            if(node is null)return; //Keep this callback pending while its asset is unavailable.
            if(state.Definition.Type==EnbRslStateType.Bone)
            {
                state.Definition.Properties.TryGetValue("BONE",out string? path);
                if(activate)EnbCurrentRetailRslBoneAttachmentRuntime.Activate(owner,node.Runtime,path);
                else EnbCurrentRetailRslBoneAttachmentRuntime.Deactivate(owner,node.Runtime,path);
                if(ReferenceEquals(_rslBoneOwners.GetValueOrDefault((obj.RenderIdentity,obj.RenderObjectGeneration)),owner))
                    owner.SceneHistory.Add(new(update.ObjectSceneEvents.Single(e=>e.Kind==EnbCurrentRetailRslObjectSceneEventKind.BoneHidden&&
                        e.StateId==action.StateId&&e.EffectiveActionCount==owner.ActionCursor+1).Ordinal+1,
                    owner.ActionCursor+1,
                    update.EffectiveActionPacketOrdinals.TryGetValue(owner.ActionCursor,out int packet)?packet:null,
                    Interlocked.Increment(ref _legoPhysicalOperationSequence),CaptureBoneMembership(owner)));
            }
            else if(activate)
            {
                if(!virtualActivation && owner.CurrentStateId!=action.StateId)continue;
                node.Runtime.SetHidden(false);
                if(virtualActivation)
                {
                    owner.Current?.Runtime.SetParent(null);
                    owner.Current=node;
                    owner.CurrentStateId=action.StateId;
                    node.Runtime.SetParent(owner.Anchor);
                    node.Runtime.SetInheritedProperty(owner,true);
                }
            }
            else node.Runtime.SetHidden(true);
        }
        if(!ApplyCreationBias())return;
        if((actionLimit is null?update.CurrentObjectStateId:selectedObject) is int current && owner.CurrentStateId!=current)
        {
            var node=Resolve(current);
            if(node is null)return;
            owner.Current?.Runtime.SetParent(null);
            owner.Current=node;
            owner.CurrentStateId=current;
            node.Runtime.SetParent(owner.Anchor);
            node.Runtime.SetInheritedProperty(owner,true);
        }
        owner.Diagnostic=null;
    }

    private static LiveRslBoneNode BuildRslBoneNode(LiveCpuRoot root)
    {
        var retained=new Dictionary<IEnbCurrentRetailRslBoneRenderObject,LiveRslBoneNode>();
        var inlineMembers=new Dictionary<IEnbCurrentRetailRslBoneRenderObject,EnbW3dRenderHlodMember>();
        if(root.Collection is not null)
        {
            var members=new List<EnbCurrentRetailRslBoneMember>();
            foreach(var childRoot in root.CollectionChildren)
            {
                var child=BuildRslBoneNode(childRoot);retained.Add(child.Runtime,child);
                members.Add(new(0,child.Runtime));
            }
            return new(root,null,new(null,aggregate:members,isCollection:true),[],retained,inlineMembers);
        }
        var hierarchy=root.Resource.Hierarchies.FirstOrDefault(h=>
            string.Equals(h.Name,root.AttachmentHierarchyName,StringComparison.OrdinalIgnoreCase));
        //Only HLOD/HMODEL owns these hierarchy child virtuals. A standalone
        //mesh may share the resource's hierarchy, but its B0 getter returns0.
        var hmodel=root.Resource.HierarchyModels.FirstOrDefault(m=>
            string.Equals(m.Name,root.RootRenderObjectName,StringComparison.OrdinalIgnoreCase));
        if(root.Hlod is null && hmodel is null)hierarchy=null;
        var levels=new List<IEnumerable<EnbCurrentRetailRslBoneMember>>();
        var aggregate=new List<EnbCurrentRetailRslBoneMember>();
        var children=new Dictionary<IEnbCurrentRetailRslBoneRenderObject,LiveRslBoneChild>();
        int ordinal=0;
        EnbCurrentRetailRslBoneMember Inline(string name,int pivot)
        {
            int memberOrdinal=ordinal++;
            //98E290/98E300 enumerate the retained level arrays, after
            //98C7A0 has skipped failed original HMODEL acquisitions.
            var nested=root.HmodelChildren.FirstOrDefault(child=>child.StorageOrdinal==memberOrdinal);
            if(nested is not null)
            {
                var child=BuildRslBoneNode(nested.Root);retained.Add(child.Runtime,child);
                inlineMembers.Add(child.Runtime,new((uint)pivot,name));
                return new(pivot,child.Runtime);
            }
            //0093E920 installs00B111A4:68/74/78/84/B0 inherit the
            //base false/zero/null capabilities. Do not confer HLOD behavior.
            //NULL prototype00941580/+0C009415E0 creates00B11388;
            //the same child and bone virtuals are inherited base no-ops.
            //Emitter0097D180 installs00B12CD8 with these same capabilities.
            //Dazzle0096B9D0 installs00B123BC:68/74/78/84/B0 are the
            //same base leaf methods;12C/00462E90 also ignores LOD bias.
            //Special-material00B16B0C likewise inherits the base child/bone
            //methods and the no-op LOD-bias setter12C/00462E90.
            if(!string.Equals(name,"NULL",StringComparison.OrdinalIgnoreCase) &&
                !root.Resource.ExternalRenderObjects.Any(e=>e.Kind is EnbW3dExternalRenderObjectKind.ParticleEmitter or
                    EnbW3dExternalRenderObjectKind.NullObject or EnbW3dExternalRenderObjectKind.Box or
                    EnbW3dExternalRenderObjectKind.Dazzle or EnbW3dExternalRenderObjectKind.SpecialMaterial &&
                    string.Equals(e.Name,name,StringComparison.OrdinalIgnoreCase)) &&
                !root.Resource.Meshes.Any(m=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,name)))
                throw new NotSupportedException($"Bone path member '{name}' requires its RenderObj class virtuals before retained attachment traversal.");
            bool emitter=root.Resource.ExternalRenderObjects.Any(e=>e.Kind==EnbW3dExternalRenderObjectKind.ParticleEmitter &&
                string.Equals(e.Name,name,StringComparison.OrdinalIgnoreCase));
            var runtime=new EnbCurrentRetailRslBoneRenderObjectRuntime(null,hasLeafLodBias:emitter,unclampedLodBias:emitter,isParticleEmitter:emitter);
            inlineMembers.Add(runtime,new((uint)pivot,name));
            return new(pivot,runtime);
        }
        if(root.Hlod is { } hlod)
        {
            foreach(var level in hlod.Levels)
                levels.Add(level.Members.Select(m=>Inline(m.Name,checked((int)m.ReferenceIndex))).ToArray());
            if(hlod.Aggregate is { } inlineAggregate)
                foreach(var member in inlineAggregate.Members)aggregate.Add(Inline(member.Name,checked((int)member.ReferenceIndex)));
        }
        else if(hmodel is not null)
            levels.Add(hmodel.Nodes.Select(n=>Inline(n.RenderObjectName,n.PivotIndex)).ToArray());
        foreach(var attachment in root.AggregateAttachments)
        {
            if(hierarchy is null)throw new NotSupportedException($"Bone owner '{root.RootRenderObjectName}' has children without a hierarchy.");
            bool emitter=attachment.Exhaust is not null || root.Resource.ExternalRenderObjects.Any(e=>
                e.Kind==EnbW3dExternalRenderObjectKind.ParticleEmitter &&
                string.Equals(e.Name,attachment.SubobjectName,StringComparison.OrdinalIgnoreCase));
            int pivot=EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy,attachment.BoneName);
            //TryGetCpuRootCore puts ordinary aggregate emitters into the HLOD
            //aggregate lane. Its no-op attachment record is a metadata mirror,
            //not a second native RenderObj child. Preserve every lane member.
            if(emitter && attachment.Exhaust is null && root.Hlod?.Aggregate?.Members.Any(m=>
                m.ReferenceIndex==(uint)pivot && string.Equals(m.Name,attachment.SubobjectName,StringComparison.OrdinalIgnoreCase))==true)continue;
            LiveRslBoneNode? child=attachment.Root is { } childRoot?BuildRslBoneNode(childRoot):null;
            // Sphere00B10458 inherits the base child/bone virtuals at68/74/78/84/B0.
            // Keep its concrete attachment and draw owner while exposing a leaf.
            if(child is null && !emitter && attachment.Sphere is null && !string.Equals(attachment.SubobjectName,"NULL",StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"Bone path child '{attachment.SubobjectName}' requires its RenderObj class virtuals.");
            var runtime=child?.Runtime??new EnbCurrentRetailRslBoneRenderObjectRuntime(null,
                hasLeafLodBias:emitter||attachment.Sphere is not null,unclampedLodBias:emitter,isParticleEmitter:emitter);
            aggregate.Add(new(pivot,runtime));
            children.Add(runtime,new(attachment,child));
        }
        return new(root,hierarchy,new(hierarchy,levels,aggregate,
            hasLeafLodBias:root.RetainedEmitter is not null||root.Sphere is not null||root.Ring is not null,
            unclampedLodBias:root.RetainedEmitter is not null,initialLodBias:root.LodBias,isParticleEmitter:root.RetainedEmitter is not null),children,retained,inlineMembers);
    }

    private LiveCpuRoot WithRslBoneAttachments(IEnbRenderObject obj,int stateId,LiveCpuRoot root)
    {
        if(!_rslBoneOwners.TryGetValue((obj.RenderIdentity,obj.RenderObjectGeneration),out var owner) ||
            !owner.Nodes.TryGetValue(stateId,out var node) || !ReferenceEquals(node.Root,root))return root;
        var states=owner.NodesByRuntime;
        try{return ExportRslBoneNode(node,states);}
        catch(Exception ex) when(ex is InvalidDataException or NotSupportedException)
        {
            //A failed optional child's bounds/publication must not invalidate
            //the last complete retained graph or abort the entire frame.
            owner.Diagnostic=ex.Message;
            return node.Exported??root;
        }
    }

    private static LiveCpuRoot ExportRslBoneNode(LiveRslBoneNode parent,
        IReadOnlyDictionary<EnbCurrentRetailRslBoneRenderObjectRuntime,LiveRslBoneNode> states)
    {
        if(RetainRslDefinitions && parent.Exported is {} retained && parent.ExportedRevision==parent.Runtime.DefinitionRevision)return retained;
        ulong revision=parent.Runtime.DefinitionRevision;
        var result=RebuildRslBoneNode(parent,states);
        parent.ExportedRevision=revision;
        return result;
    }

    private static LiveCpuRoot RebuildRslBoneNode(LiveRslBoneNode parent,
        IReadOnlyDictionary<EnbCurrentRetailRslBoneRenderObjectRuntime,LiveRslBoneNode> states)
    {
        var previous=parent.Exported??=parent.Root with {RslBoneNode=parent};
        bool sameHidden=previous.RenderObjectHidden==parent.Runtime.Hidden && previous.LodBias==parent.Runtime.LodBias;
        LiveRslBoneNode? Resolve(IEnbCurrentRetailRslBoneRenderObject runtime)
            =>parent.RetainedChildren.TryGetValue(runtime,out var retained)?retained:
                runtime is EnbCurrentRetailRslBoneRenderObjectRuntime value&&states.TryGetValue(value,out var state)?state:null;
        if(parent.Runtime.IsCollection)
        {
            var children=parent.Runtime.Aggregate.Select(member=>
            {
                var child=Resolve(member.Child)??throw new InvalidOperationException("Collection child has no retained render owner.");
                var exported=ExportRslBoneNode(child,states);
                return parent.RetainedChildren.ContainsKey(member.Child)?exported:child.IndependentExport(exported);
            }).ToArray();
            if(sameHidden&&children.SequenceEqual(previous.CollectionChildren))return previous;
            return parent.Exported=parent.Root with {CollectionChildren=children,RenderObjectHidden=parent.Runtime.Hidden,RslBoneNode=parent};
        }
        if(parent.Hierarchy is null)return sameHidden?previous:
            parent.Exported=parent.Root with {RenderObjectHidden=parent.Runtime.Hidden,LodBias=parent.Runtime.LodBias,RslBoneNode=parent};
        var attachments=new List<LiveAggregateAttachment>();
        foreach(var member in parent.Runtime.Aggregate)
        {
            if(member.Child is EnbCurrentRetailRslBoneRenderObjectRuntime retainedRuntime&&states.TryGetValue(retainedRuntime,out var retainedState))
            {
                parent.DynamicChildren[retainedRuntime]=retainedState;
                //Hidden RenderObjs still contribute to98F1D0 bounds.
                ExportRslBoneNode(retainedState,states);
            }
            if(member.Child is not EnbCurrentRetailRslBoneRenderObjectRuntime runtime)continue;
            if(parent.InlineMembers.ContainsKey(runtime))continue;
            if(parent.Children.TryGetValue(runtime,out var authored))
            {
                attachments.Add(authored.Attachment with{Root=authored.Node is { } child?ExportRslBoneNode(child,states):null});
            }
            else if(states.TryGetValue(runtime,out var child))
            {
                attachments.Add(new(child.Root.RuntimeRenderObjectName,parent.Hierarchy.Pivots[member.BoneIndex].Name,
                    child.IndependentExport(ExportRslBoneNode(child,states)),IsRetailNoOp:false){RslChild=runtime,RslStateId=child.StateId});
            }
        }
        if(parent.Root.Hlod is not {} hlod)return sameHidden&&attachments.SequenceEqual(previous.AggregateAttachments)?previous:
            parent.Exported=parent.Root with {AggregateAttachments=attachments,RenderObjectHidden=parent.Runtime.Hidden,LodBias=parent.Runtime.LodBias,RslBoneNode=parent};
        var composites=new List<EnbW3dRenderCompositeChild>();
        var retainedChildren=new List<LiveHmodelChild>();int ordinal=0;
        IReadOnlyList<EnbW3dRenderHlodMember> Members(IEnumerable<EnbCurrentRetailRslBoneMember> members,int? level,bool aggregate)
        {
            var result=new List<EnbW3dRenderHlodMember>();
            foreach(var member in members)
            {
                if(member.Child is not EnbCurrentRetailRslBoneRenderObjectRuntime {Hidden:false} runtime ||
                    !parent.InlineMembers.TryGetValue(runtime,out var original))continue;
                result.Add(original with {ReferenceIndex=(uint)member.BoneIndex});
                if(parent.RetainedChildren.TryGetValue(runtime,out var child))
                {
                    var root=ExportRslBoneNode(child,states);
                    if(!TryDescribeCompositeChild(root,original.Name,out var composite,out var diagnostic))
                        throw new InvalidDataException(diagnostic);
                    if(!composites.Any(value=>string.Equals(value.Name,original.Name,StringComparison.OrdinalIgnoreCase)))composites.Add(composite);
                    retainedChildren.Add(new(ordinal,(uint)member.BoneIndex,root,level,aggregate));
                }
                ordinal++;
            }
            return result;
        }
        var levels=hlod.Levels.Select((level,index)=>level with {Members=Members(parent.Runtime.Levels[index],level.Ordinal,false)}).ToArray();
        var aggregate=hlod.Aggregate is {} authoredAggregate
            ?authoredAggregate with {Members=Members(parent.Runtime.Aggregate,null,true)}:null;
        bool sameMembers=levels.Length==previous.Hlod!.Levels.Count&&
            levels.Select((level,index)=>level.Members.SequenceEqual(previous.Hlod.Levels[index].Members)).All(value=>value)&&
            (aggregate?.Members??[]).SequenceEqual(previous.Hlod.Aggregate?.Members??[]);
        if(sameHidden&&sameMembers&&retainedChildren.SequenceEqual(previous.HmodelChildren)&&attachments.SequenceEqual(previous.AggregateAttachments))return previous;
        var updated=hlod with {Levels=levels,Aggregate=aggregate};
        return parent.Exported=parent.Root with {Hlod=updated,HmodelChildren=retainedChildren,AggregateAttachments=attachments,RenderObjectHidden=parent.Runtime.Hidden,LodBias=parent.Runtime.LodBias,RslBoneNode=parent,
            Resource=parent.Root.Resource.Copy().SetHierarchicalLods([updated]).SetCompositeRenderChildren(composites)};
    }

    private static Dictionary<string,(uint Pivot,EnbW3dRenderCompositeChild Child)> RslBoneCompositeMembers(LiveCpuRoot root)
    {
        if(root.RslBoneNode is not {} node)return new();
        if(RetainRslDefinitions && node.CompositeMembers is {} cached &&
            node.CompositeRevision==node.Runtime.DefinitionRevision && ReferenceEquals(node.CompositeExport,node.Exported))return cached;
        var result=new Dictionary<string,(uint,EnbW3dRenderCompositeChild)>();
        foreach(var member in node.Runtime.Aggregate)
        {
            if(member.Child is not EnbCurrentRetailRslBoneRenderObjectRuntime runtime ||
                !node.DynamicChildren.TryGetValue(runtime,out var child))continue;
            var childRoot=child.Exported??child.Root;
            var descendants=RslBoneCompositeMembers(childRoot);
            if(childRoot.Hlod is not null && descendants.Count!=0)
            {
                var membership=new RetainedCompositeMembership(childRoot);
                foreach(var pair in descendants)membership.Attachments.Add(pair.Key,pair.Value);
                membership.Rebuild();childRoot=membership.QueryRoot;
            }
            string name=$"<rsl-bone:{child.StateId}>";
            if(!TryDescribeCompositeChild(childRoot,name,out var descriptor,out var diagnostic))throw new InvalidDataException(diagnostic);
            result.Add(name,((uint)member.BoneIndex,descriptor));
        }
        node.CompositeMembers=result;
        node.CompositeRevision=node.Runtime.DefinitionRevision;
        node.CompositeExport=node.Exported;
        return result;
    }

    private sealed class LiveRslBoneOwner:IEnbCurrentRetailRslBoneOwner
    {
        public readonly Dictionary<int,LiveRslBoneNode> Nodes=[];
        public readonly Dictionary<EnbCurrentRetailRslBoneRenderObjectRuntime,LiveRslBoneNode> NodesByRuntime=[];
        public void AddNode(int id,LiveRslBoneNode node)
        {Nodes.Add(id,node);NodesByRuntime.Add(node.Runtime,node);}
        public readonly EnbCurrentRetailRslBoneRenderObjectRuntime Anchor=new(null);
        public readonly List<BoneSceneCheckpoint> SceneHistory=[];
        public int ActionCursor;
        public bool CreationBiasApplied;
        public int? CurrentStateId;
        public LiveRslBoneNode? Current;
        public string? Diagnostic;
        public IEnbCurrentRetailRslBoneRenderObject? CurrentRenderObject=>Current?.Runtime;
        public object GetInheritedProperty()=>this;
        public bool RemoveChild(IEnbCurrentRetailRslBoneRenderObject child)=>Current?.Runtime.RemoveChild(child)??false;
    }
    private sealed record LiveRslBoneNode(LiveCpuRoot Root,EnbW3dRenderHierarchy? Hierarchy,
        EnbCurrentRetailRslBoneRenderObjectRuntime Runtime,
        Dictionary<IEnbCurrentRetailRslBoneRenderObject,LiveRslBoneChild> Children,
        Dictionary<IEnbCurrentRetailRslBoneRenderObject,LiveRslBoneNode> RetainedChildren,
        Dictionary<IEnbCurrentRetailRslBoneRenderObject,EnbW3dRenderHlodMember> InlineMembers)
    {
        public Dictionary<EnbCurrentRetailRslBoneRenderObjectRuntime,LiveRslBoneNode> DynamicChildren {get;}=[];
        public int? StateId {get;set;}
        public event Action? ExportChanged;
        private LiveCpuRoot? _exported;
        public LiveCpuRoot? Exported
        {
            get=>_exported;
            set
            {
                if(ReferenceEquals(_exported,value))return;
                _exported=value;
                if(SceneModel is {} model && value is not null)model.Root=value;
                ExportChanged?.Invoke();
            }
        }
        public ulong ExportedRevision=ulong.MaxValue;
        public Dictionary<string,(uint Pivot,EnbW3dRenderCompositeChild Child)>? CompositeMembers;
        public ulong CompositeRevision;
        public LiveCpuRoot? CompositeExport;
        private LiveCpuRoot? _independentSource;
        private LiveCpuRoot? _independentExport;
        public LiveCpuRoot IndependentExport(LiveCpuRoot source)
        {
            // The independent factory marker is structural, not a query input.
            // Publish once per root generation; queued queries retain the old root.
            if(source.IndependentRslFactory)return source;
            if(!ReferenceEquals(_independentSource,source))
            {
                _independentSource=source;
                _independentExport=source with {IndependentRslFactory=true};
            }
            return _independentExport!;
        }
        public LegoPhysicalModel? SceneModel;
        public IReadOnlyList<EnbCurrentRetailRslBoneMember>[] InitialLevels {get;}=Runtime.Levels.Select(level=>(IReadOnlyList<EnbCurrentRetailRslBoneMember>)level.ToArray()).ToArray();
        public IReadOnlyList<EnbCurrentRetailRslBoneMember> InitialAggregate {get;}=Runtime.Aggregate.ToArray();
    }
    private sealed record LiveRslBoneChild(LiveAggregateAttachment Attachment,LiveRslBoneNode? Node);
}
