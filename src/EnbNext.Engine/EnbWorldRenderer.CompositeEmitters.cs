using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly List<(CompositeParticle,EnbRenderDetailOwner)> _detailCompositeParticles=[];
    private readonly Dictionary<(uint Effect, ulong Generation, ulong Sequence, int Model), EffectCompositeParticles> _effectCompositeParticles=[];
    private readonly EnbCurrentRetailSceneCallbackList<(EffectCompositeParticles Group,CompositeParticle Particle,bool Buffer)> _compositeCallbacks;
    private readonly HashSet<EffectCompositeParticles> _trackedCompositeCallbacks=[];

    private long _compositeRemovalSerial;
    private readonly Dictionary<(uint,ulong,ulong), CompositeBoundsState> _effectCompositeBounds=[];
    private sealed class CompositeBoundsState
    {
        public long Serial,Revision;
        public readonly EnbW3dHlodQueryOwner Admission=new();
        private readonly List<EnbW3dHlodQueryOwner> candidates=[];
        public EnbW3dHlodQueryOwner Candidate(int child)
        {
            int slot=child+1;while(candidates.Count<=slot)candidates.Add(new());return candidates[slot];
        }
        public EnbEffectCompositeBoundsHistory? History;
        public EnbCurrentRetailRObjEffectAsset? SourceAsset,PublishedAsset;
        public long PublishedRevision=-1;
        public EnbCurrentRetailRObjEffectAsset Publish(EnbCurrentRetailRObjEffectAsset source,EnbCurrentRetailRObjEffectAsset result)
        {SourceAsset=source;PublishedAsset=result;PublishedRevision=Revision;return result;}
    }
    private CompositeBoundsState CompositeBoundsFor((uint,ulong,ulong) key)
    {
        if(!_effectCompositeBounds.TryGetValue(key,out var state))_effectCompositeBounds.Add(key,state=new());
        return state;
    }

    private sealed class EffectCompositeParticles(IEnbRenderObject owner, float scale, uint clock,
        IReadOnlyList<EnbRObjCompositeEmitter> definitions,CompositeBoundsState boundsOwner,string occurrencePath,bool standalone=false)
    {
        public readonly CompositeBoundsState BoundsOwner=boundsOwner;
        public readonly IEnbRenderObject Owner=owner;
        public readonly float Scale=scale;
        public readonly uint Start=clock;
        public uint? LastCallback;
        public uint PreparedClock;
        public EffectObjectInstance? Standalone;
        public readonly CompositeParticle[] Particles=CreateParticles(owner,definitions,occurrencePath,standalone);
    }

    private static CompositeParticle[] CreateParticles(IEnbRenderObject owner,IReadOnlyList<EnbRObjCompositeEmitter> definitions,string path,bool standalone)
    {
        var particles=new CompositeParticle[definitions.Count];
        for(int i=0;i<particles.Length;i++)particles[i]=new(owner,i,definitions[i],standalone?path:$"{path}/{i}");
        return particles;
    }

    private sealed class CompositeParticle : IEnbParticleRenderOwner
    {
        public EnbParticleBufferDetailBudgetSemantics.Owner? DetailOwner;
        private readonly IEnbRenderObject RenderOwner;
        private readonly int RenderOrdinal;
        public readonly EnbRObjCompositeEmitter Definition;
        string IEnbParticleRenderOwner.RenderObjectName=>Definition.Asset.Definition.Name;
        string IEnbParticleRenderOwner.EmitterResourceName=>Definition.Asset.Key.ResourceName;
        uint IEnbParticleRenderOwner.GameId=>RenderOwner.GameId;
        EnbRenderPassKind IEnbParticleRenderOwner.RenderPass=>EnbRenderPassKind.OrdinaryWorld;
        EnbCurrentRetailParticleEmitterDefinition IEnbParticleRenderOwner.Definition=>Definition.Asset.Definition;
        EnbW3dRenderTextureLocation? IEnbParticleRenderOwner.TextureLocation=>Definition.Asset.Texture;
        Matrix4x4 IEnbParticleRenderOwner.WorldTransform=>World;
        string IEnbParticleRenderOwner.HierarchyName=>"";
        int IEnbParticleRenderOwner.PivotIndex=>Definition.Asset.PivotIndex;
        int IEnbParticleRenderOwner.BindingOrdinal=>RenderOrdinal;
        string? IEnbParticleRenderOwner.RuntimeTextureName=>Definition.Asset.RuntimeTextureName;
        EnbResolvedTextureResource? IEnbParticleRenderOwner.RetainedTexture=>Definition.Asset.RetainedTexture;

        private EnbCurrentRetailParticleEmitterRuntime.GeometryOwner? _geometry;
        public EnbCurrentRetailParticleEmitterRuntime.GeometryOwner Geometry=>_geometry??=new(Definition.Asset.Definition,RenderInstancePath);
        public readonly EnbRetailParticleSceneLifetime? State;
        public Matrix4x4 World=Matrix4x4.Identity;
        public string? Diagnostic;
        public readonly uint Capacity;
        public readonly int LevelCount;
        public int Level;
        public float ProjectedDetail;
        public EnbW3dWorldBounds Bounds;
        public bool SceneVisible;
        public bool Retired;
        public uint? EmitterRemovalClock;
        public Action<bool>? SceneChanged;
        public long RemovalSerial;
        public readonly string RenderInstancePath;
        public CompositeParticle(IEnbRenderObject owner,int ordinal,EnbRObjCompositeEmitter definition,string renderInstancePath)
        {
            RenderOwner=owner;RenderOrdinal=ordinal;Definition=definition;RenderInstancePath=renderInstancePath;
            Diagnostic=definition.Diagnostic;
            try
            {
                if(definition.Asset.Definition.MaximumEmissions==0)
                    EnbCurrentRetailShipParticleRuntime.Validate(definition.Asset.Definition,allowInheritedMotion:true);
                else EnbCurrentRetailRObjParticleAsset.ValidateAnimated(definition.Asset.Definition);
                State=new(definition.Asset.Definition,0,randomOwner:definition.FactoryRandomOwner?.Clone());
                Capacity=EnbRetailParticleEmissionArithmetic.Capacity(definition.Asset.Definition);
                LevelCount=EnbParticleBufferDetailBudgetSemantics.ResolveCopiedLevelCount(Capacity);
                Level=LevelCount-1;
            }
            catch(InvalidDataException ex){Diagnostic??=ex.Message;}
        }
    }

    private readonly List<EnbDazzleCollisionRoot> _compositeBufferCollision=[];

    private void TrackCompositeCallbacks(EffectCompositeParticles group)
    {
        if(!_trackedCompositeCallbacks.Add(group))return;
        foreach(var particle in group.Particles)
        {
            if(particle.State is not {} state)continue;
            particle.SceneChanged=attached=>
            {
                if(attached)_compositeCallbacks.Register((group,particle,false));
                else _compositeCallbacks.Remove((group,particle,false));
            };
            state.SceneAttachmentChanged+=particle.SceneChanged;
            if(state.SceneAttached)_compositeCallbacks.Register((group,particle,false));
            if(state.BufferAttached)_compositeCallbacks.Register((group,particle,true));
        }
    }

    private void ForgetCompositeCallbacks(EffectCompositeParticles group)
    {
        if(!_trackedCompositeCallbacks.Remove(group))return;
        foreach(var particle in group.Particles)
        {
            if(particle.State is {} state && particle.SceneChanged is {} changed)state.SceneAttachmentChanged-=changed;
            particle.SceneChanged=null;
            _compositeCallbacks.Remove((group,particle,false));
            _compositeCallbacks.Remove((group,particle,true));
        }
    }

    private void ClearCompositeCallbacks()
    {
        foreach(var group in _trackedCompositeCallbacks.ToArray())ForgetCompositeCallbacks(group);
        _compositeCallbacks.Clear();
    }

    private Dictionary<CompositeParticle,string> UpdateCompositeCallbacks(HashSet<(uint,ulong,ulong)> active,uint clock,
        out HashSet<CompositeParticle> retiring)
    {
        retiring=[];
        var retired=retiring;
        var failures=new Dictionary<CompositeParticle,string>();
        var groups=_effectCompositeParticles.Values.ToHashSet();
        foreach(var stale in _trackedCompositeCallbacks.Where(g=>!groups.Contains(g)).ToArray())ForgetCompositeCallbacks(stale);
        foreach(var (key,group) in _effectCompositeParticles)
        {
            TrackCompositeCallbacks(group);
            bool attached=active.Contains((key.Effect,key.Generation,key.Sequence)) && group.PreparedClock==clock;
            uint age=unchecked(clock-group.Start);
            for(int i=0;i<group.Particles.Length;i++)
            {
                var particle=group.Particles[i];
                if(particle.Retired || particle.State is not {} state)continue;
                try
                {
                    if(!attached)state.Destroy(age,particle.World,group.Scale);
                    if(state.Destroyed&&!state.BufferAttached){particle.Retired=true;retired.Add(particle);}
                    if(state.BufferAttached)CompositeBufferBounds(group,particle,i,age);
                }
                catch(InvalidDataException error){failures[particle]=error.Message;}
            }
        }
        //98DF20/98DFA0 register children;97DEF0 attaches a separately
        //registered buffer.9661C0 visits the mutable list, not factory order.
        _compositeCallbacks.SetCallback(entry=>
        {
            var (group,particle,buffer)=entry;
            if(failures.ContainsKey(particle)||particle.Retired)return;
            var state=particle.State!;uint age=unchecked(clock-group.Start);
            try
            {
                if(buffer)
                {
                    state.BufferCallback(age,group.LastCallback is {} previous?unchecked(clock-previous):0,particle.World,group.Scale);
                    if(state.CanRetire)
                    {
                        particle.Retired=true;retired.Add(particle);
                        QueueParticleSceneRemoval(state,true,()=>
                        {
                            _compositeCallbacks.Remove((group,particle,true));
                            _compositeCallbacks.Remove((group,particle,false));
                        });
                    }
                }
                else
                {
                    if(state.EmitterCallback(particle.World))_compositeCallbacks.Register((group,particle,true));
                    if(state.SceneAttached&&state.Emitter.HasCompletedFiniteRun)
                    {
                        if(group.Standalone is {} standalone)
                        {
                            if(standalone.EmitterRemovalClock is null)
                            {
                                standalone.EmitterRemovalClock=clock;
                                QueueParticleSceneRemoval(state,false,()=>state.SetSceneAttached(false));
                            }
                        }
                        else if(particle.EmitterRemovalClock is null)
                        {
                            particle.EmitterRemovalClock=clock;
                            QueueParticleSceneRemoval(state,false,()=>
                            {
                                if(state.Destroyed)return;
                                state.Destroy(age,particle.World,group.Scale);
                                particle.RemovalSerial=++_compositeRemovalSerial;
                                group.BoundsOwner.Revision++;
                            });
                        }
                    }
                }
            }
            catch(InvalidDataException error){failures[particle]=error.Message;}
        });
        _particleCallbackFinishes.Add(()=>
        {
        foreach(var group in groups)
        {
            group.LastCallback=clock;
        }
        });
        return failures;
    }

    //98E0F0 removes the actual child occurrence;98F1D0 recomputes bounds
    //from the remaining children. Keep prototype data and sibling occurrences intact.
    private EnbW3dRenderResource RetainedCompositeResource(EnbCurrentRetailEffectOwner node,int model,EnbW3dRenderResource resource,long through=long.MaxValue)
    {
        if(!(_shipExhaustOwner??this)._effectCompositeParticles.TryGetValue((node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence,model),out var group))return resource;
        var removed=group.Particles.Where(p=>p.RemovalSerial>0 && p.RemovalSerial<=through)
            .Select(p=>(p.Definition.LevelOrdinal,p.Definition.MemberOrdinal)).ToHashSet();
        if(removed.Count==0)return resource;
        return resource.Copy().SetHierarchicalLods(resource.HierarchicalLods.Select(h=>h with
        {
            Levels=h.Levels.Select(l=>l with {Members=l.Members.Where((m,i)=>!removed.Contains((l.Ordinal,i))).ToArray()}).ToArray(),
            Aggregate=h.Aggregate is {} a?a with {Members=a.Members.Where((m,i)=>!removed.Contains((-1,i))).ToArray()}:null
        }).ToArray());
    }

    private EnbCurrentRetailRObjEffectAsset RetainedCompositeAsset(EnbCurrentRetailEffectOwner node,EnbCurrentRetailRObjEffectAsset asset)
        =>ResolveCompositeAsset(node,asset,out _);

    private EnbCurrentRetailRObjEffectAsset ResolveCompositeAsset(EnbCurrentRetailEffectOwner node,
        EnbCurrentRetailRObjEffectAsset asset,out CompositeBoundsState cached)
    {
        var source=_shipExhaustOwner??this;
        if(asset.Mixed is not null || asset.Resource is not null && asset.AggregateParticles is not null)
        {
            if(!source._sceneParticleAssets.TryGetValue(asset,out var prepared))
                prepared=source._sceneParticleAssets.GetValue(asset,source.CreateSceneParticleAsset);
            asset=prepared;
        }
        var key=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
        cached=source.CompositeBoundsFor(key);
        if(ReferenceEquals(cached.SourceAsset,asset) && cached.PublishedRevision==cached.Revision)
            return cached.PublishedAsset!;
        return source.RebuildCompositeAsset(node,asset,cached);
    }
    private EnbCurrentRetailRObjEffectAsset RebuildCompositeAsset(EnbCurrentRetailEffectOwner node,
        EnbCurrentRetailRObjEffectAsset asset,CompositeBoundsState cached)
    {
        var source=this;
        var key=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
        var removals=source._effectCompositeParticles.Where(pair=>pair.Key.Effect==key.Item1 &&
            pair.Key.Generation==key.Item2 && pair.Key.Sequence==key.Item3)
            .SelectMany(pair=>pair.Value.Particles.Where(p=>p.RemovalSerial>0).Select(p=>(Model:pair.Key.Model,Particle:p)))
            .OrderBy(pair=>pair.Particle.RemovalSerial).ToArray();
        if(removals.Length==0 || asset.Resource is null)return cached.Publish(asset,asset);
        var history=cached.History??new EnbEffectCompositeBoundsHistory(asset);
        EnbW3dRenderResource[] Current(long through)=>[RetainedCompositeResource(node,-1,asset.Resource,through),
            ..asset.ModelAttachments.Select((m,i)=>RetainedCompositeResource(node,i,m.Asset.Resource!,through))];
        foreach(var removal in removals.Where(r=>r.Particle.RemovalSerial>cached.Serial))
            history.Remove(removal.Model,Current(removal.Particle.RemovalSerial));
        cached.Serial=removals[^1].Particle.RemovalSerial;cached.History=history;
        var resources=history.Project(Current(long.MaxValue));
        return cached.Publish(asset,asset with {Resource=resources[0],RetainedBoundsResource=history.Bounds(-1),
            ModelAttachments=asset.ModelAttachments.Select((model,index)=>model with {Asset=model.Asset with
                {Resource=resources[index+1],RetainedBoundsResource=history.Bounds(index)}}).ToArray()});
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<EnbCurrentRetailRObjEffectAsset,EnbCurrentRetailRObjEffectAsset> _sceneParticleAssets=new();

    private EnbW3dRenderResource RetainedCompositeQueryResource(EnbCurrentRetailEffectOwner node,int model,
        EnbW3dRenderResource mapperResource,EnbCurrentRetailRObjEffectAsset sceneAsset)
    {
        if(sceneAsset.CompositeEmitters.Count==0 || sceneAsset.Resource is not {} sceneResource)
            return RetainedCompositeResource(node,model,mapperResource);
        //Keep the retained mesh models, but query the actual child array.
        //The factory mapper resource predates live emitter projection/removal.
        //98F1D0/98E680 use the remaining children for bounds/ray traversal.
        //48BE60 localizes each mesh occurrence. Its internal binding name
        //must survive the live particle projection too: the authored name
        //no longer resolves in that configured mesh table.
        var retained=RetainedCompositeResource(node,model,mapperResource);
        var retainedHlods=retained.HierarchicalLods.Count!=0?retained.HierarchicalLods:
            retained.HierarchyModels.Select(h=>EnbW3dHmodelConversion.Create(h,_=>true)).ToArray();
        IReadOnlyList<EnbW3dRenderHlodMember> Join(IReadOnlyList<EnbW3dRenderHlodMember> scene,
            IReadOnlyList<EnbW3dRenderHlodMember>? configured)
            =>scene.Select((member,index)=>configured is not null && index<configured.Count &&
                sceneResource.Meshes.Any(mesh=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,member.Name))
                    ?member with {Name=configured[index].Name}:member).ToArray();
        var hlods=sceneResource.HierarchicalLods.Select((hlod,index)=>
        {
            var configured=index<retainedHlods.Count?retainedHlods[index]:null;
            return hlod with
            {
                Levels=hlod.Levels.Select(level=>level with {Members=Join(level.Members,
                    configured?.Levels.FirstOrDefault(l=>l.Ordinal==level.Ordinal)?.Members)}).ToArray(),
                Aggregate=hlod.Aggregate is {} aggregate?aggregate with {Members=Join(aggregate.Members,configured?.Aggregate?.Members)}:null
            };
        }).ToArray();
        return mapperResource.Copy().SetHierarchyModels(sceneResource.HierarchyModels).SetHierarchicalLods(hlods).SetExternalRenderObjects(sceneResource.ExternalRenderObjects).SetCompositeRenderChildren(sceneResource.CompositeRenderChildren);
    }

    private EnbCurrentRetailRObjEffectAsset CreateSceneParticleAsset(EnbCurrentRetailRObjEffectAsset asset)
    {
        //The immutable preparation retains the authored mixed description for
        //factory configuration. Its live scene projection has one ordinary
        //buffer owner per emitter:009A3A50/0098E4B0,97DEF0,9662E0.
        var mixed=asset.Mixed;
        var resource=asset.Resource!;
        var particles=asset.AggregateParticles??mixed!.Sprites;
        var construction=mixed is null?particles.Select(p=>new EnbW3dRenderHlodMember((uint)p.PivotIndex,p.Definition.Name)).ToArray():
            mixed.ParticleConstructionOrder.Select(member=>member.Streak
                ? new EnbW3dRenderHlodMember((uint)mixed.Streaks[member.Index].PivotIndex,mixed.Streaks[member.Index].Definition.Name)
                : new EnbW3dRenderHlodMember((uint)mixed.Sprites[member.Index].PivotIndex,mixed.Sprites[member.Index].Definition.Name)).ToArray();
        if(resource.HierarchyModels.SingleOrDefault() is {} model)
        {
            var hlod=EnbW3dHmodelConversion.Create(model,_=>true);
            resource=resource.Copy().SetHierarchyModels([]).SetHierarchicalLods([hlod with {Aggregate=new(0,construction)}]);
        }
        else if(resource.HierarchicalLods.Single().Aggregate is null)
            resource=resource.Copy().SetHierarchicalLods([resource.HierarchicalLods.Single() with {Aggregate=new(0,construction)}]);
        var external=resource.ExternalRenderObjects.ToList();
        foreach(var particle in particles)
            if(!external.Any(e=>e.Name.Equals(particle.Definition.Name,StringComparison.OrdinalIgnoreCase)))
                external.Add(new(particle.Definition.Name,EnbW3dExternalRenderObjectKind.ParticleEmitter,particle.Key.ResourceName,particle.Key.ArchivePath)
                    {ParticleEmitterDefinition=particle.Definition});
        resource=resource.Copy().SetExternalRenderObjects(external);
        var emitters=asset.CompositeEmitters.ToList();
        var aggregate=resource.HierarchicalLods.Single().Aggregate;
        for(int ordinal=0;ordinal<(aggregate?.Members.Count??0);ordinal++)
        {
            var member=aggregate!.Members[ordinal];
            var particle=particles.FirstOrDefault(p=>p.Definition.Name.Equals(member.Name,StringComparison.OrdinalIgnoreCase));
            if(particle is null && mixed?.Streaks.FirstOrDefault(p=>p.Definition.Name.Equals(member.Name,StringComparison.OrdinalIgnoreCase)) is {} streak)
                particle=new(streak.Key,streak.Texture,streak.Definition,streak.PivotIndex)
                    {RuntimeTextureName=streak.RuntimeTextureName,RetainedTexture=streak.RetainedTexture};
            if(particle is not null)emitters.Add(new(particle with {PivotIndex=(int)member.ReferenceIndex},-1,ordinal));
        }
        return asset with {Resource=resource,Mixed=null,AggregateParticles=null,
            CompositeEmitters=emitters,
            Rings=asset.Rings is {} rings ? rings with {Resource=resource,Emitters=null}:null,
            Spheres=asset.Spheres is {} spheres ? spheres with {Resource=resource,Emitters=null}:null};
    }

    private static EnbW3dWorldBounds CompositeBufferBounds(EffectCompositeParticles group,CompositeParticle particle,int index,uint age)
    {
        var state=particle.State!;var asset=particle.Definition.Asset;
        if(!state.BoundsDirty)return particle.Bounds;
        state.EnsureBounds(age);
        float size=EnbRetailParticleBufferBounds.MaximumSize(asset.Definition.Size,
            EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(asset.Definition.LifetimeSeconds),group.Scale);
        if(!EnbCurrentRetailParticleEmitterRuntime.TryComputeBufferBounds(particle,state.BorrowLiveBirths(),0,age,group.Scale,state.MotionHistory,size,out var bounds,out var error))
            throw new InvalidDataException(error);

        return particle.Bounds=bounds
            .WorldSphere(asset.Definition.LocalSpace?particle.World:Matrix4x4.Identity);
    }

    private List<(CompositeParticle Owner,EnbRenderDetailOwner Candidate)> PrepareEffectCompositeParticleCandidates(
        EnbResolvedRenderCamera camera,uint clock)
    {
        var result=_detailCompositeParticles;result.Clear();
        if(_shipExhaustOwner is not null)return result;
        foreach(var (key,group) in (_shipExhaustOwner??this)._effectCompositeParticles)
        for(int i=0;i<group.Particles.Length;i++)
        {
            var particle=group.Particles[i];var state=particle.State;var asset=particle.Definition.Asset;
            particle.SceneVisible=false;
            if(state is null || particle.Retired || !state.BufferAttached || particle.Diagnostic is not null)continue;
            uint age=unchecked(clock-group.Start);
            EnbW3dWorldBounds bounds;
            try { bounds=CompositeBufferBounds(group,particle,i,age); }
            catch(InvalidDataException error){particle.Diagnostic=error.Message;continue;}
            float size=EnbRetailParticleBufferBounds.MaximumSize(asset.Definition.Size,
                EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(asset.Definition.LifetimeSeconds),group.Scale);
            if(asset.Definition.RenderMode is not (3 or 4) && FrustumCullingEnabled && !EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(camera,bounds))continue;
            particle.SceneVisible=true;
            if(!EnbParticleBufferDetailBudgetSemantics.TryComputeProjectedDetail(camera,bounds,particle.Capacity,size,particle.ProjectedDetail,out var projected))continue;
            particle.ProjectedDetail=projected.Smoothed;
            result.Add((particle,(particle.DetailOwner ??= new($"effect-composite/{key}/{i}",particle.Capacity,(int)asset.Definition.RenderMode,particle.LevelCount)).Update(particle.Level,particle.ProjectedDetail,1f)));
        }
        return result;
    }

    private void PrepareStandaloneEffectParticle(EnbCurrentRetailEffectOwner node,IEnbRenderObject owner,
        EnbCurrentRetailRObjParticleAsset asset,EffectObjectInstance instance,Matrix4x4 world,uint clock)
    {
        if(_shipExhaustOwner is not null)return;
        var key=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence,-2);
        if(!_effectCompositeParticles.TryGetValue(key,out var group))
        {
            var bounds=CompositeBoundsFor((key.Item1,key.Item2,key.Item3));bounds.Revision++;
            _effectCompositeParticles.Add(key,group=new(owner,instance.Radius,clock,[new(asset,-1,0)],bounds,$"effect-particle/{key.Item1}/{key.Item2}/{key.Item3}",true) {Standalone=instance});
        }
        TrackCompositeCallbacks(group);
        group.PreparedClock=clock;
        var particle=group.Particles[0];particle.World=world;
        if(particle.State is not {} state)return;
        state.UpdateTransform(world);
        bool removed=instance.EmitterRemovalClock is not null;
        state.SetSceneAttached(!removed);
    }

    private void PrepareEffectCompositeParticles(EnbCurrentRetailEffectOwner node,IEnbRenderObject owner,
        EnbCurrentRetailRObjEffectAsset asset, int model, Matrix4x4 rigidWorld, float scale,
        EnbW3dHlodSelection? selection,uint clock,EnbCurrentRetailRslAnimationSequenceRuntime.Sample? observed=null,bool transformsOnly=false,float? hierarchySourceScale=null,bool parentSceneAttached=true,bool constructForScene=false)
    {
        if(_shipExhaustOwner is not null || asset.CompositeEmitters.Count==0)return;
        var key=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence,model);
        if(!_effectCompositeParticles.TryGetValue(key,out var retained))
        {
            if(transformsOnly&&!constructForScene)return;
            var bounds=CompositeBoundsFor((key.Item1,key.Item2,key.Item3));bounds.Revision++;
            _effectCompositeParticles.Add(key,retained=new(owner,scale,clock,asset.CompositeEmitters,bounds,$"effect-composite/{key.Item1}/{key.Item2}/{key.Item3}/{model}"));
        }
        TrackCompositeCallbacks(retained);
        retained.PreparedClock=clock;
        if(retained.Particles.All(p=>p.State?.Destroyed==true))return;
        float frame=0;string? error=null;var animation=asset.Animation;
        if(asset.Sequence is not null)
        {
            if(observed is not {} sample)throw new InvalidDataException("Composite emitter animation observation unavailable");
            animation=sample.BoundAnimation;frame=sample.Frame;
        }
        else if(animation is not null && !EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(animation,asset.AnimationFlags!,node.ActualStartTick,clock,out frame,out error))
            throw new InvalidDataException(error);
        //0098EF60 scales stored hierarchy translations and emitter data;
        //0098F090 publishes a rigid child matrix, not a uniformly scaled basis.
        if(!EnbW3dHierarchyPose.TryEvaluate(asset.Resource!.Hierarchies.Single(),rigidWorld,animation,frame,null,
            out var pose,out error,storedTranslationScale:asset.ResolveHierarchyScale(scale,hierarchySourceScale)))throw new InvalidDataException(error);
        int level=selection?.LevelOrdinal??(asset.Resource.HierarchicalLods.SingleOrDefault() is {} hlod
            ? EnbW3dHlodBudgetCompiler.CurrentRetailInitialLevel(hlod):0);
        uint age=unchecked(clock-retained.Start);
        foreach(var particle in retained.Particles)
        {
            if(particle.State is not {} state)continue;
            if(state.Destroyed)continue;
            int pivot=particle.Definition.Asset.PivotIndex;
            particle.World=pose.WorldTransforms[pivot];
            state.UpdateTransform(particle.World);
            if(transformsOnly&&!constructForScene)continue;
            //98ED30 changes scene membership on a level switch;98DF20/
            //98DFA0 forward that operation through nested current children.
            if(!constructForScene&&_effectHlodSceneAssets.ContainsKey(EffectHlodKey.For(node,owner)))
            {
                var world=particle.World;bool hidden=!pose.Visibility[pivot];
                _effectRenderSceneChanges.Add(()=>state.SetAnimationHidden(age,world,scale,hidden));
            }
            else
            {
                if(!constructForScene)state.SetSceneAttached(parentSceneAttached && (particle.Definition.LevelOrdinal<0 || particle.Definition.LevelOrdinal==level));
                state.SetAnimationHidden(age,particle.World,scale,!pose.Visibility[pivot]);
            }
        }
    }

    private void ApplyEffectCompositeParticles(List<EnbW3dDrawTask> packets,IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,
        IEnbSceneFrame snapshot,EnbW3dLightEnvironment lights,EnbResolvedRenderCamera camera,uint clock,
        ref int draws,ref int blocked,ref string? failure)
    {
        _compositeBufferCollision.Clear();
        var source=_shipExhaustOwner??this;
        var active=nodes.Where(n=>n.Active).Select(n=>(n.Root.EffectId,n.Root.InstanceGeneration,n.Sequence)).ToHashSet();
        var retiring=_shipExhaustOwner is null?_compositeCallbackRetiring:new HashSet<CompositeParticle>();
        var callbackFailures=_shipExhaustOwner is null?_compositeCallbackFailures:new Dictionary<CompositeParticle,string>();
        foreach(var (key,group) in source._effectCompositeParticles.ToArray())
        {
            //A proxy does not acquire independent world effects or their tails
            //merely because they once used the selected object as a source.
            if(_shipExhaustOwner is not null && !active.Contains((key.Effect,key.Generation,key.Sequence)))continue;
            uint age=unchecked(clock-group.Start);
            bool attached=active.Contains((key.Effect,key.Generation,key.Sequence)) && group.PreparedClock==clock;
            for(int i=0;i<group.Particles.Length;i++)
            {
                var particle=group.Particles[i];var state=particle.State;var asset=particle.Definition.Asset;
                try
                {
                    if(state is null)throw new InvalidDataException(particle.Diagnostic);
                    if(particle.Retired&&!retiring.Contains(particle))continue;
                    if(callbackFailures.TryGetValue(particle,out var callbackError))throw new InvalidDataException(callbackError);
                    if(attached && group.Standalone is not null && state.SceneAttached)
                        RetainEmitterSceneRoot(null,particle.World,camera,clock,0,0);
                    if(particle.Diagnostic is {} diagnostic)throw new InvalidDataException(diagnostic);
                    //95C310 is checked in the callback;965E00(type2) removes
                    //after traversal, not before this frame's ray/render use.
                    if(!state.BufferAttached || !particle.SceneVisible && _shipExhaustOwner is null)continue;
                    int index=i;
                    _compositeBufferCollision.Add(new(default,true,false,static (start,end,fraction,inside)=>(fraction,inside))
                    {
                        ResolveSphere=()=>
                        {
                            try { var bounds=CompositeBufferBounds(group,particle,index,age);return new Vector4(bounds.Center,bounds.Radius); }
                            catch(InvalidDataException error){particle.Diagnostic=error.Message;return null;}
                        }
                    });
                    state.AdvanceForRender(age);
                    if(!EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(asset.Key,group.Owner.RenderIdentity,group.Owner.VisualGeneration,
                        particle,camera,lights,0,0,age,out var packet,out _,out var error,particleSizeScale:group.Scale,
                        worldBirths:state.BorrowLiveBirths(),motionHistory:state.MotionHistory,
                        activeEmissionRun:state.Emitter.HasLineEndpoint?state.Emitter.EmissionRun:null,
                        internalDetailLevel:particle.LevelCount-1-particle.Level,
                        currentRetailFactoryScale:true,cullToCamera:FrustumCullingEnabled,
                        frameStreams:RetainEffectParticleStorage?_particleStreamFrame:null,geometryOwner:particle.Geometry))throw new InvalidDataException(error);
                    if(packet is not null)
                    {
                        if(RetainEffectParticleStorage)_retainedEffectParticlePackets++;
                        packets.Add(packet);draws++;
                    }
                }
                catch(InvalidDataException ex){blocked++;failure??=$"Composite emitter {asset.Definition.Name}: {ex.Message}";}
            }
            if(_shipExhaustOwner is null && !attached && group.Particles.All(p=>p.State is null || p.Retired))
            {
                ForgetCompositeCallbacks(group);
                group.BoundsOwner.Revision++;
                source._effectCompositeParticles.Remove(key);
            }
        }
    }
}
