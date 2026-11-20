using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<(uint,ulong,ulong),EffectRslOwner> _effectRslOwners=[];
    private readonly Dictionary<ulong,EffectRslOwner> _effectRslRootOwners=[];
    private readonly EnbCurrentRetailSceneCallbackList<(ulong Identity,ulong Generation)> _effectRslCallbacks;
    private readonly HashSet<(ulong Identity,ulong Generation)> _effectRslCallbackMembers=[];
    private readonly Dictionary<EnbW3dRenderMesh,EnbCurrentRetailRObjMeshMappers> _effectRslMeshModels=new(ReferenceEqualityComparer.Instance);
    private static long _nextEffectRslIdentity;
    private string _effectRslStatus="effect RSL idle";
    private IReadOnlyList<EnbCurrentRetailEffectOwner> _frameEffectRslNodes=[];
    private sealed class EffectRslOwner(EnbRenderObjectState state,EnbResolvedRslReference reference)
    {
        public readonly EnbRenderObjectState Object=state;
        private EnbRenderObjectState? secondaryInvocation;
        public EnbRenderObjectState Invocation(bool secondary)=>secondary?secondaryInvocation??=CreateRenderInvocation(Object):Object;
        public readonly EnbResolvedRslReference Reference=reference;
        public EnbRslObjectState? Update {get=>Attachment.CurrentState;set=>Attachment.CurrentState=value;}
        public RslWrapperOwner Attachment {get;}=new(preserveWhileResident:true);
        public int? LocalizedState;
        public LiveCpuRoot? LocalizedRoot;
        public readonly List<EnbCurrentRetailRObjMeshMappers> Models=[];
        public bool MappersPrepared, MappersActivated;
        public float SourceRadiusScale=1;
    }

    private void AppendEffectRslRoots(LiveFrame frame,EnbRetailAssetBundle assets,
        IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,RootTaskList roots,
        List<RslAnimationDiagnostic> animations,List<LiveRslSoundRequest> sounds,
        OwnerPublicationMap<EnbRslObjectState> updates,
        OwnerPublicationMap<EnbCurrentRetailRslEffectPassLibrary> passes,EnbRetainedMembershipSet<ulong> live,
        EnbRetainedMembershipSet<(ulong RenderIdentity,ulong VisualGeneration)> generations,
        Dictionary<(ulong Identity,ulong Generation),Action> callbacks,
        Dictionary<ulong,Action> rootBuilders,List<Action> soundCollectors)
    {
        if(_shipExhaustOwner is {} main)
        {
            var visibleEffects=frame.Snapshot.Effects.Select(e=>(e.EffectId,e.InstanceGeneration)).ToHashSet();
            nodes=main._frameEffectRslNodes.Where(n=>visibleEffects.Contains((n.Root.EffectId,n.Root.InstanceGeneration))).ToArray();
        }
        else _frameEffectRslNodes=nodes;
        var used=new HashSet<(uint,ulong,ulong)>();
        var attached=new HashSet<(ulong Identity,ulong Generation)>();
        int added=0,pending=0,blocked=0;string? failure=null;
        var rslNodes=nodes.Where(n=>n.TryGetRObjDefinition(out var d,out _) &&
            assets.PresentationCatalog!.ResolveBaseAsset(d.BaseAssetId).Kind==EnbBaseAssetDefinitionKind.Rsl).ToArray();
        if(rslNodes.Length!=0)
        {
            for(int sourceIndex=0;sourceIndex<nodes.Count;sourceIndex++)nodes[sourceIndex].UpdateSource(SceneObjects(frame.Snapshot));
            UpdateCombatEffectPoses(nodes,frame.Snapshot,assets,roots,frame.CurrentClientTime);
        }
        var radii=BuildCombatEffectRadii(roots,assets,SceneObjects(frame.Snapshot));
        foreach(var node in rslNodes)
        {
            var key=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);used.Add(key);
            try
            {
                node.TryGetRObjDefinition(out var definition,out _);
                var task=(_shipExhaustOwner??this).LoadEffectObject(assets,definition.BaseAssetId,definition.HsvShift,node:node,currentClientTime:frame.CurrentClientTime);
                if(task is null || !task.IsCompleted){pending++;continue;}
                var reference=task.GetAwaiter().GetResult().RslOwner;
                if(reference is null)continue;
                var source=ResolveCombatResourceOwner(node,SceneObjects(frame.Snapshot));
                if(source?.Spatial?.Transform is not {} spatial)continue;
                if(!EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(spatial,out var pose,out var why))
                    throw new InvalidDataException(why);
                pose=PlaceCombatEffect(node,definition,ResolveCombatEffectPose(node,pose),frame.Camera,radii.GetValueOrDefault(source.RenderIdentity));
                var owners=(_shipExhaustOwner??this)._effectRslOwners;
                if(!owners.TryGetValue(key,out var owner))
                {
                    if(_shipExhaustOwner is not null)continue;
                    if(definition.ScaleToRadius && !IsMatrixBoundEffect(node) && !IsCoordinateBoundEffect(node) && !radii.ContainsKey(source.RenderIdentity))
                        throw new InvalidDataException("RSL effect source radius unavailable");
                    //Separate from packet identities and the descending
                    //ulong.MaxValue orbital/terrain presentation identities.
                    ulong serial;uint soundIdentity;
                    do {serial=(ulong)Interlocked.Increment(ref _nextEffectRslIdentity);soundIdentity=uint.MaxValue-unchecked((uint)serial);}
                    while(SceneObjects(frame.Snapshot).Any(o=>o.GameId==soundIdentity));
                    ulong identity=(1UL<<63)|serial;
                    var obj=new EnbRenderObjectState
                    {
                        RenderIdentity=identity,GameId=soundIdentity,BaseAsset=definition.BaseAssetId,
                        Scale=ResolveCombatRenderScale(node,definition,radii.GetValueOrDefault(source.RenderIdentity)),
                        HsvH=definition.HsvShift.X,HsvS=definition.HsvShift.Y,HsvV=definition.HsvShift.Z,
                        Radar=source.Radar,Spatial=new EnbRenderSpatialState(),RenderPoseSlot=-1,
                        Decals=[],ColorizationEntries=[],RenderStateLinks=[],ActiveRenderStates=[],RenderStateActions=[],
                        Attachments=[],SubParts=[]
                    };
                    ((EnbRenderSpatialState)obj.Spatial).AdoptDefinition(source.Spatial);
                    owners.Add(key,owner=new(obj,reference));
                    owner.SourceRadiusScale=ResolveCombatRenderScale(node,definition with {AssetScale=1},radii.GetValueOrDefault(source.RenderIdentity));
                    _effectRslRootOwners.Add(identity,owner);
                }
                var current=owner.Invocation(_shipExhaustOwner is not null);
                ((EnbRenderSpatialState)current.Spatial!).SetWorldTransform(spatial,pose);
                live.Add(current.RenderIdentity);generations.Add((current.RenderIdentity,current.VisualGeneration));
                if(_shipExhaustOwner is null)
                {

                    var pass=RetainRslPassOwner(current,owner.Reference,assets,prepareWholeLibrary:true);
                    if(pass is not null)passes[current.RenderIdentity]=pass;
                    PrepareRslObjectFactories(current,owner.Reference,assets,definition.FactoryInputs??(definition.AssetScale,definition.HsvShift));
                    if(definition.ResetMappers && !owner.MappersPrepared)
                    {
                        //6EA830 localizes after BASE construction/initial RSID,
                        //before6EF200 activation or any scene callback.
                        owner.Update=_effectRslObjectRuntime.Advance(current,owner.Reference,[],frame.CurrentClientTime,null,
                            (animationId,objectId)=>ResolveRetainedRslAnimationClip(current,owner.Reference,assets,animationId,objectId),
                            advanceCallbacks:false);
                        if(owner.Update.CurrentObjectStateId is int initial &&
                            (owner.Update.Graph??owner.Reference.ResourceGraph)!.TryFindState(initial,out var state))
                        {
                            if(!TryMaterializeRslObjectRoot(current.BaseAsset,initial,state,assets,out var root,out var error))
                                throw new InvalidDataException(error);
                            root=ResolveConfiguredRslRoot(current,initial,root);
                            uint clock=unchecked(ReadMapperClock()-(frame.CurrentClientTime-node.ConstructionTick));
                            try { owner.LocalizedRoot=LocalizeEffectRslChildren(root,owner,clock,skipRootMesh:true); }
                            catch { ReleaseEffectRslModels(owner,removeOwner:false); throw; }
                            owner.LocalizedState=initial;
                        }
                        owner.MappersPrepared=true;
                    }
                    if(definition.ResetMappers && node.Active && !owner.MappersActivated)
                    {
                        uint clock=unchecked(ReadMapperClock()-(frame.CurrentClientTime-node.ActualStartTick));
                        foreach(var model in owner.Models)model.Activate(clock);
                        owner.MappersActivated=true;
                    }
                    //Construction/placement precedes the scene traversal.
                    //6EF200 attaches this same461600 wrapper to9661C0; its
                    //controller must not run ahead of retained particle buffers.
                    owner.Update=_effectRslObjectRuntime.Advance(current,owner.Reference,[],frame.CurrentClientTime,null,
                        (animationId,objectId)=>ResolveRetainedRslAnimationClip(current,owner.Reference,assets,animationId,objectId),
                        advanceCallbacks:false,
                        soundCompletedAtActivation:(id,tick,ordinal)=>_rslSoundCompletions.TryGetValue(RslSoundKey(current,id,tick),out var completion) && completion.PlaybackOrdinal==ordinal);
                }
                else if(_shipExhaustOwner._frameRslPassOwners.TryGetValue(current.RenderIdentity,out var sharedPass))
                    passes[current.RenderIdentity]=sharedPass;
                if(owner.Update is null)continue;
                void Publish()
                {
                    var update=owner.Update!;
                    updates[current.RenderIdentity]=update;
                    _rslEffectiveActionsByRenderIdentity[current.RenderIdentity]=update.Actions;
                    if(!_ownedSceneFrame)_retainedEmitterRslUpdates[(current.RenderIdentity,current.VisualGeneration)]=update;
                    ConsumeRslBoneActions(current,owner.Reference,assets,update);
                    if(_shipExhaustOwner is null&&_legoScenes.ContainsKey((current.RenderIdentity,current.RenderObjectGeneration)))
                        SynchronizeLegoScene(current,update,assets,node.Active);
                    if(update.Counters.FailClosedEvents!=0)throw new InvalidDataException(update.Counters.Diagnostic);
                }
                Publish();
                soundCollectors.Add(()=>
                {
                    try {AppendRetainedRslSounds(current,owner.Reference,
                        owner.Update!,assets,sounds,forceHidden:!node.Active);}
                    catch(Exception ex){blocked++;failure??=ex.Message;PublishStatus();}
                });
                int priorCount=0;
                void BuildRoots()
                {
                    added-=priorCount;priorCount=0;
                    try
                    {
                        var update=owner.Update!;
                        if(!node.Active || update.CurrentObjectHidden || update.CurrentObjectStateId is null)return;
                        int count=AddRslRoots(current,owner.Reference,assets,roots,animations,sounds,frame.CurrentClientTime,update,out var error,out bool waiting);
                        if(waiting)pending++;
                        else if(count==0)throw new InvalidDataException(error);
                        added+=priorCount=count;
                    }
                    catch(Exception ex){blocked++;failure??=ex.Message;}
                    finally {PublishStatus();}
                }
                rootBuilders[current.RenderIdentity]=BuildRoots;
                BuildRoots();
                if(_shipExhaustOwner is null && node.Active)
                {
                    var callbackKey=(current.RenderIdentity,current.RenderObjectGeneration);
                    attached.Add(callbackKey);
                    if(_effectRslCallbackMembers.Add(callbackKey))_effectRslCallbacks.Register(callbackKey);
                    SetEffectWrapperAttached(owner,true);
                    //6EF200 ->461600 registers the wrapper before its selected
                    //RenderObj. Its children use the same scene mutation path
                    //as world-owned RSL models, including intermediate Bones.
                    RetainLegoPhysicalOwner(current);
                    SynchronizeLegoScene(current,owner.Update,assets,true);
                    callbacks[callbackKey]=()=>
                    {
                        try
                        {
                            owner.Update=_effectRslObjectRuntime.Advance(current,owner.Reference,[],frame.CurrentClientTime,null,
                                (animationId,objectId)=>ResolveRetainedRslAnimationClip(current,owner.Reference,assets,animationId,objectId),
                                advanceCallbacks:true,
                                soundCompletedAtActivation:(id,tick,ordinal)=>_rslSoundCompletions.TryGetValue(RslSoundKey(current,id,tick),out var completion) && completion.PlaybackOrdinal==ordinal);
                            Publish();
                        }
                        catch(Exception ex){blocked++;failure??=ex.Message;PublishStatus();}
                    };
                }
            }
            catch(Exception ex){blocked++;failure??=ex.Message;}
        }
        foreach(var key in _effectRslOwners.Keys.Where(k=>!used.Contains(k)).ToArray())
        {
            uint soundIdentity=_effectRslOwners[key].Object.GameId;
            foreach(var sound in _rslSoundCompletions.Keys.Where(k=>k.GameId==soundIdentity).ToArray())RemoveSoundCompletion(sound);
            ReleaseEffectRslModels(_effectRslOwners[key]);
            _effectRslOwners.Remove(key);
        }
        foreach(var key in _effectRslCallbackMembers.Where(k=>!attached.Contains(k)).ToArray())
        {_effectRslCallbacks.Remove(key);_effectRslCallbackMembers.Remove(key);if(_effectRslRootOwners.TryGetValue(key.Identity,out var owner))SetEffectWrapperAttached(owner,false);}
        PublishStatus();
        void PublishStatus()=>_effectRslStatus=$"effect RSL roots {added} pending {pending} blocked {blocked}"+(failure is null?"":" · "+failure);
    }

    private LiveCpuRoot ResolveEffectRslRoot(IEnbRenderObject obj,int stateId,LiveCpuRoot root)
    {
        var view=_shipExhaustOwner??this;
        root=view._effectRslRootOwners.TryGetValue(obj.RenderIdentity,out var owner) &&
            owner.LocalizedState==stateId && owner.LocalizedRoot is {} localized ? localized : ResolveConfiguredRslRoot(obj,stateId,root);
        var wrapper=view.LegoRslOwnerFor(obj.RenderIdentity);
        //4612D0 forwards LOD bias only to the model selected when the setter
        //runs. A subsequently selected state retains its own bias.
        return wrapper?.Models.TryGetValue(stateId,out var model)==true?SetFactoryLodBias(root,model.Root.LodBias):root;
    }

    private LiveCpuRoot LocalizeEffectRslChildren(LiveCpuRoot root,EffectRslOwner owner,uint clock,bool skipRootMesh=false)
    {
        //462090/4620C0 delegate to the current object's child virtuals:
        //a direct current Mesh is not visited; HLOD visits every level/member.
        var resource=root.Resource;
        var hlod=root.Hlod;
        if(hlod is not null || !skipRootMesh && root.IsDirectMeshPrototype)
        {
            var meshes=hlod is not null ? resource.Meshes : resource.Meshes.Where(m=>
                EnbW3dRenderObjectNameSemantics.MatchesMesh(m,root.RootRenderObjectName)).ToArray();
            var selected=resource.Copy().SetMeshes(meshes).SetHierarchicalLods(hlod is null?[]:[hlod]).SetHierarchyModels([]);
            using var acquired=meshes.Count!=0 && _configuredDirectMeshModels.TryGetValue(meshes[0],out var configured)
                ?configured.RetainRenderObjects(selected)
                :new EnbCurrentRetailRObjMeshMappers(selected,clock,CameraMeshBatches,
                    _prototypeResources,meshes.Select(m=>(EnbW3dRenderMesh?)m).ToArray());
            var localized=acquired.Localize(clock,compositeOccurrences:hlod is not null);
            owner.Models.Add(localized);
            foreach(var mesh in localized.Resource.Meshes)_effectRslMeshModels.Add(mesh,localized);
            resource=localized.Resource;
            hlod=resource.HierarchicalLods.FirstOrDefault();
        }
        return root with {Resource=resource,Hlod=hlod,
            CollectionChildren=root.CollectionChildren.Select(c=>LocalizeEffectRslChildren(c,owner,clock)).ToArray(),
            HmodelChildren=root.HmodelChildren.Select(c=>c with {Root=LocalizeEffectRslChildren(c.Root,owner,clock)}).ToArray(),
            AggregateAttachments=root.AggregateAttachments.Select(a=>a.Root is {} child?
                a with {Root=LocalizeEffectRslChildren(child,owner,clock)}:a).ToArray()};
    }

    private void ReleaseEffectRslModels(EffectRslOwner owner,bool removeOwner=true)
    {
        if(removeOwner)
        {
            owner.Attachment.Retired=true;QueueWrapperAttachment(owner.Attachment);
            _effectRslRootOwners.Remove(owner.Object.RenderIdentity);
            var key=(owner.Object.RenderIdentity,owner.Object.RenderObjectGeneration);
            _compatLegoScenes.Remove(key);
            if(_legoScenes.Remove(key,out var scene)){ReleaseLegoSceneDependencies(scene);ChangeLegoSceneTree(scene.Tree,null);}
            _legoPhysicalOwners.Remove(owner.Object.RenderIdentity);
            _effectRslCallbacks.Remove(key);_effectRslCallbackMembers.Remove(key);
        }
        foreach(var model in owner.Models)
        {
            foreach(var mesh in model.Resource.Meshes)_effectRslMeshModels.Remove(mesh);
            model.Dispose();
        }
        owner.Models.Clear();
    }

    private void AppendOrdinaryRslSounds(IEnbRenderObject obj,EnbResolvedRslReference reference,
        EnbRslObjectState update,EnbRetailAssetBundle assets,List<LiveRslSoundRequest> sounds)
    {
        int first=sounds.Count;
        AppendRetainedRslSounds(obj,reference,update,assets,sounds);
        //461650 removes scene callbacks;45FC50 alone starts/follows the sound.
        //Keep the retained request so an already playing handle is not released.
        if(!_ordinaryRslCallbackMembers.Contains((obj.RenderIdentity,obj.RenderObjectGeneration)))
            for(int i=first;i<sounds.Count;i++)sounds[i]=sounds[i] with {UpdateEnabled=false};
    }

    private void AppendRetainedRslSounds(IEnbRenderObject obj,EnbResolvedRslReference reference,
        EnbRslObjectState update,EnbRetailAssetBundle assets,List<LiveRslSoundRequest> sounds,bool forceHidden=false)
    {
        var graph=update.Graph??reference.ResourceGraph!;
        ReconcileRslSoundSelections(obj,update,graph,assets);
        foreach(int id in update.ActiveList.Snapshot())
        {
            if(update.CompletedSounds.Contains(id))continue;
            if(!graph.TryFindState(id,out var state) || state.Definition.Type!=EnbRslStateType.Sound)continue;
            int ordinal=Enumerable.Range(0,update.Actions.Count).LastOrDefault(i=>update.Actions[i].StateId==id &&
                update.Actions[i].Opcode==0x0030 && update.ActivationVirtualActionOrdinals.Contains(i),-1);
            var activation=ordinal>=0?update.Actions[ordinal]:default;
            DescribeR6Z30SoundActivationResolution(assets,graph,obj,id,state,activation.ClientTime,out var request, describe: false);
            if(request is not null)sounds.Add(request with
            {
                UpdateEnabled=!forceHidden && update.CurrentObjectStateId is not null && !update.CurrentObjectHidden,
                PlaybackOrdinal=ordinal+1
            });
        }
    }
}
