using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly List<(TunnelParticle,EnbRenderDetailOwner)> _detailTunnelParticles=[];
    private readonly Dictionary<int,Task<EnbWarpTunnelAsset>> _warpTunnelAssets=[];
    private readonly Dictionary<(uint,ulong,ulong),WarpTunnelInstance> _warpTunnels=[];
    private readonly EnbCurrentRetailSceneCallbackList<((uint,ulong,ulong) Key,int Child,bool Buffer)> _tunnelCallbacks;
    private readonly List<EffectDazzleCollision> _warpTunnelCollision=[];
    private readonly List<EnbDazzleCollisionRoot> _warpTunnelBufferCollision=[];
    private string _warpTunnelStatus="warp tunnel idle";
    private int _warpTunnelPending,_warpTunnelBlocked;
    private string? _warpTunnelFailure;
    private void ClearWarpTunnels()
    {
        _warpTunnels.Clear();_tunnelCallbacks.Clear();_warpTunnelAssets.Clear();_warpTunnelCollision.Clear();_warpTunnelBufferCollision.Clear();
        _warpTunnelStatus="warp tunnel idle";
    }

    private void TrackTunnelCallbacks((uint,ulong,ulong) key,int index,TunnelParticle child)
    {
        child.Buffer.SceneAttachmentChanged+=attached=>
        {
            if(attached)_tunnelCallbacks.Register((key,index,false));
            else _tunnelCallbacks.Remove((key,index,false));
        };
    }

    private void AdvanceTunnelCallbacks(uint now)
    {
        //98DF20 forwards child scene entry in retained order. Each97DEF0
        //callback prepends its independent buffer for the next9661C0 walk.
        _tunnelCallbacks.SetCallback(entry=>
        {
            var state=_warpTunnels[entry.Key];var child=state.Children[entry.Child];
            if(child.Diagnostic is not null)return;
            uint age=unchecked(now-state.Node.ActualStartTick);
            try
            {
                if(entry.Buffer)
                {
                    child.Buffer.BufferCallback(age,child.LastAge is {} previous?unchecked(age-previous):0,child.World,1);
                    if(child.Buffer.CanRetire)QueueParticleSceneRemoval(child.Buffer,true,()=>
                    {
                        child.Retired=true;
                        _tunnelCallbacks.Remove((entry.Key,entry.Child,false));_tunnelCallbacks.Remove((entry.Key,entry.Child,true));
                    });
                }
                else
                {
                    if(child.Buffer.EmitterCallback(child.World))_tunnelCallbacks.Register((entry.Key,entry.Child,true));
                    if(child.Buffer.SceneAttached && child.Buffer.Emitter.HasCompletedFiniteRun && child.RemovalClock is null)
                    {
                        child.RemovalClock=now;
                        QueueParticleSceneRemoval(child.Buffer,false,()=>child.Buffer.Destroy(age,child.World,1));
                    }
                }
            }
            catch(InvalidDataException error){child.Diagnostic=error.Message;}
        });
        _particleCallbackFinishes.Add(()=>
        {
        foreach(var (key,state) in _warpTunnels)
        for(int i=0;i<state.Children.Length;i++)
        {
            var child=state.Children[i];uint age=unchecked(now-state.Node.ActualStartTick);
            child.LastAge=age;
            if(child.Buffer.Destroyed&&!child.Buffer.BufferAttached)
            {child.Retired=true;_tunnelCallbacks.Remove((key,i,false));}
        }
        });
    }

    private List<(TunnelParticle Owner,EnbRenderDetailOwner Candidate)> PrepareWarpTunnelCandidates(
        IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,IEnbSceneFrame snapshot,EnbRetailAssetBundle assets,
        EnbResolvedRenderCamera camera,uint now,out float fixedCost)
    {
        var result=_detailTunnelParticles;result.Clear();fixedCost=0;
        if(_shipExhaustOwner is not null)return result;
        _warpTunnelPending=_warpTunnelBlocked=0;_warpTunnelFailure=null;
        var source=_shipExhaustOwner??this;
        var used=nodes.Where(n=>n.Definition.EffectClassKind==EnbEffectClassKind.WarpEffect)
            .ToDictionary(n=>(n.Root.EffectId,n.Root.InstanceGeneration,n.Sequence));
        foreach(var (key,node) in _shipExhaustOwner is null?used:[])
        {
            try
            {
                var p=node.Definition.Properties;
                if(node.Root.GeometryKind!=EnbSceneEffectGeometryKind.Object || node.BoundGameId is null or 0 ||
                    node.Root.SpatialOffset!=Vector3.Zero || node.Root.EffectOffset!=Vector3.Zero || node.Root.Scale!=1 ||
                    node.Root.HsvShift!=Vector3.Zero || !string.IsNullOrEmpty(node.Root.BoneName) ||
                    EnbRetailIniDocument.ReadBool(p,"ScaleToRadius") || EnbRetailIniDocument.ReadBool(p,"FaceCamera") || EnbRetailIniDocument.ReadBool(p,"InFrontOfObject") ||
                    (p.ContainsKey("TrackSource")&&!EnbRetailIniDocument.ReadBool(p,"TrackSource")) || node.Definition.BaseAssetId is not int id)
                    throw new InvalidDataException("WarpEffect requires ordinary unscaled source tracking");
                if(!_warpTunnelAssets.TryGetValue(id,out var task))
                {
                    if(_warpTunnelAssets.Count>=16)throw new InvalidDataException("Warp tunnel asset bound exceeded");
                    _warpTunnelAssets.Add(id,task=LoadVisualAsset(()=>EnbWarpTunnelAsset.Load(assets,id)));
                }
                if(!node.Active)continue;
                var owner=SceneObjects(snapshot).FirstOrDefault(o=>o.GameId==node.BoundGameId);
                if(!_warpTunnels.TryGetValue(key,out var state))_warpTunnels.Add(key,state=new(node,unchecked(ReadMapperClock()-(now-node.ActualStartTick))));
                state.Fade.Update(now,owner is not null && ((int)owner.GameId<0 || owner.ShipPresentation?.CachedWarpState==2));
                if(state.Fade.Ended)continue;
                if(owner?.Spatial?.Transform is not {} spatial || !EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(spatial,out var world,out _))continue;
                state.Owner=owner;state.World=world;
                if(!task.IsCompleted){_warpTunnelPending++;continue;}
                if(state.Asset is null)
                {
                    state.Asset=task.GetAwaiter().GetResult();
                    uint age=unchecked(now-state.Node.ActualStartTick);
                    state.Children=state.Asset.Particles.Select(a=>new TunnelParticle(a.Asset,age)).ToArray();
                    for(int i=0;i<state.Children.Length;i++)TrackTunnelCallbacks(key,i,state.Children[i]);
                }
            }
            catch(InvalidDataException error){_warpTunnelBlocked++;_warpTunnelFailure??=error.Message;}
        }
        foreach(var (key,state) in source._warpTunnels.ToArray())
        {
            if(_shipExhaustOwner is not null && !SceneObjects(snapshot).Any(o=>o.RenderIdentity==state.Owner?.RenderIdentity))continue;
            state.SceneVisible=false;
            if(_shipExhaustOwner is null)
            {
                state.Live=used.ContainsKey(key);
                state.Active=state.Live && used[key].Active && !state.Fade.Ended;
            }
            if(state.Asset is not {} asset || state.Owner is null)
            {if(_shipExhaustOwner is null&&!state.Live)_warpTunnels.Remove(key);continue;}
            try
            {
                uint age=unchecked(now-state.Node.ActualStartTick);
                if(_shipExhaustOwner is null)
                {
                    if(!EnbW3dHierarchyPose.TryEvaluate(asset.Resource.Hierarchies.Single(),state.World,out var poses,out var error))throw new InvalidDataException(error);
                    for(int i=0;i<state.Children.Length;i++)
                    {
                        var child=state.Children[i];
                        if(!state.Live || state.Fade.Ended)child.Buffer.Destroy(age,child.World,1);
                        if(child.Buffer.Destroyed)continue;
                        child.World=poses[asset.Particles[i].Pivot];child.Buffer.UpdateTransform(child.World);
                        child.Buffer.SetSceneAttached(state.Active);
                    }
                }
                state.SceneResource=asset.RetainedSceneResource(state.Children.Select((c,i)=>(c,i)).Where(p=>p.c.Buffer.Destroyed).Select(p=>p.i).ToHashSet());
                var resource=state.SceneResource;var hlod=resource.HierarchicalLods.Single();
                if(state.Active)
                {
                    if(!EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,hlod,state.World,null,0,out var bounds,out var error))throw new InvalidDataException(error);
                    state.SceneVisible=!FrustumCullingEnabled||EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(camera,bounds.WorldSphere);
                    if(state.SceneVisible)
                    {
                        if(!EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(resource,hlod,_=>true,out var costs,out error))throw new InvalidDataException(error);
                        fixedCost+=EnbW3dHlodBudgetCompiler.CurrentRetailLevelCost(costs[0]);
                    }
                }
                for(int i=0;i<state.Children.Length;i++)
                {
                    var child=state.Children[i];child.SceneVisible=false;
                    if(!child.Buffer.BufferAttached || child.Retired)continue;
                    var bounds=TunnelBufferBounds(child,age);var definition=child.Asset.Definition;
                    if(definition.RenderMode is not (3 or 4) && FrustumCullingEnabled && !EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(camera,bounds))continue;
                    child.SceneVisible=true;
                    if(!EnbParticleBufferDetailBudgetSemantics.TryComputeProjectedDetail(camera,bounds,child.Capacity,child.MaximumSize,child.ProjectedDetail,out var detail))continue;
                    child.ProjectedDetail=detail.Smoothed;
                    result.Add((child,(child.DetailOwner ??= new($"warp-tunnel/{key}/{i}",child.Capacity,(int)definition.RenderMode,child.LevelCount)).Update(child.Level,child.ProjectedDetail,1f)));
                }
            }
            catch(InvalidDataException error){_warpTunnelBlocked++;_warpTunnelFailure??=error.Message;}
        }
        return result;
    }

    private static EnbW3dWorldBounds TunnelBufferBounds(TunnelParticle child,uint age)
    {
        if(!child.Buffer.BoundsDirty)return child.Bounds;
        child.Buffer.EnsureBounds(age);
        var asset=child.Asset;
        var draw=new EnbW3dParticleEmitterDrawInstance(asset.Definition.Name,asset.Key.ResourceName,0,EnbRenderPassKind.OrdinaryWorld,
            asset.Definition,asset.Texture,child.World,"",0,0);
        if(!EnbCurrentRetailParticleEmitterRuntime.TryComputeBufferBounds(draw,child.Buffer.BorrowLiveBirths(),0,age,1,child.Buffer.MotionHistory,
            child.MaximumSize,out var bounds,out var error))throw new InvalidDataException(error);
        return child.Bounds=bounds
            .WorldSphere(asset.Definition.LocalSpace?child.World:Matrix4x4.Identity);
    }

    private void DrawWarpTunnels(List<EnbW3dDrawTask> packets,IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,
        LiveFrame frame,EnbRetailAssetBundle assets,EnbW3dLightEnvironment lights)
    {
        uint now=frame.CurrentClientTime;int draws=0,blocked=_warpTunnelBlocked;string? failure=_warpTunnelFailure;
        _warpTunnelCollision.Clear();_warpTunnelBufferCollision.Clear();
        var source=_shipExhaustOwner??this;
        foreach(var (key,state) in source._warpTunnels.ToArray())
        {
            if(state.Asset is not {} asset || state.Owner is not {} owner)continue;
            if(_shipExhaustOwner is not null && !nodes.Any(n=>n.Active && (n.Root.EffectId,n.Root.InstanceGeneration,n.Sequence)==key))continue;
            try
            {
                uint age=unchecked(now-state.Node.ActualStartTick);
                if(state.Active && state.SceneResource is {} resource)
                    _warpTunnelCollision.Add(new(new(resource,new(resource.HierarchicalLods.Single().Name,0),asset.OverbrightOff,null,null),
                        resource,state.World,1,[],frame.Camera));
                if(state.Active && state.SceneVisible)
                {
                    if(!EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner,asset.Resource,state.World,now,EnbRenderPassKind.OrdinaryWorld,null,lights,[],out var list,out var error))throw new InvalidDataException(error);
                    foreach(var authored in list.Packets)
                    {
                        var packet=_overbrightMaterials.Apply(authored,asset.OverbrightOff,out _);
                        EnbW3dCompiledMaterialSemantics Fade(EnbW3dCompiledMaterialSemantics s)=>s with {Uniforms=s.Uniforms with {Emissive=new(state.Fade.Brightness)}};
                        var material=packet.Material.Source;
                        material=(material).Copy().SetSingleBindingSemantics(material.SingleBindingSemantics is {} single?Fade(single):null).SetTriangleBindings(material.TriangleBindings.Select(t=>t with {Semantics=t.Semantics is {} s?Fade(s):null}).ToArray());
                        packets.Add((packet).ForkTask().SetMaterial(new(material,material.SingleBindingSemantics)).SetRenderInstancePath($"warp-tunnel/{key}").SetCurrentRetailEffectMapperStartMilliseconds(state.MapperStart));draws++;
                    }
                }
                for(int i=0;i<state.Children.Length;i++)
                {
                    var child=state.Children[i];var a=child.Asset;
                    if(child.Retired)continue;
                    if(child.Diagnostic is {} diagnostic)throw new InvalidDataException(diagnostic);
                    if(!child.Buffer.BufferAttached || !child.SceneVisible && _shipExhaustOwner is null)continue;
                    _warpTunnelBufferCollision.Add(new(default,true,false,static(start,end,fraction,inside)=>(fraction,inside))
                    {ResolveSphere=()=>{var bounds=TunnelBufferBounds(child,age);return new Vector4(bounds.Center,bounds.Radius);}});
                    child.Buffer.AdvanceForRender(age);
                    var emitter=new EnbW3dParticleEmitterDrawInstance(a.Definition.Name,a.Key.ResourceName,owner.GameId,EnbRenderPassKind.OrdinaryWorld,
                        a.Definition,a.Texture,child.World,"",0,i){RuntimeTextureName=a.RuntimeTextureName,RetainedTexture=a.RetainedTexture};
                    if(!EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(a.Key,owner.RenderIdentity,owner.VisualGeneration,emitter,frame.Camera,lights,
                        (uint)state.Node.Sequence+(uint)i,0,age,out var packet,out _,out var error,worldBirths:child.Buffer.BorrowLiveBirths(),
                        activeEmissionRun:child.Buffer.Emitter.HasLineEndpoint?child.Buffer.Emitter.EmissionRun:null,
                        motionHistory:child.Buffer.MotionHistory,internalDetailLevel:child.LevelCount-1-child.Level,
                        cullToCamera:FrustumCullingEnabled))throw new InvalidDataException(error);
                    if(packet is not null){packets.Add((packet).ForkTask().SetRenderInstancePath($"warp-tunnel/{key}/particle/{i}"));draws++;}
                }
                if(_shipExhaustOwner is null&&!state.Live&&state.Children.All(c=>c.Retired))_warpTunnels.Remove(key);
            }
            catch(InvalidDataException error){blocked++;failure??=error.Message;}
        }
        _warpTunnelStatus=$"warp tunnel draws {draws} pending {_warpTunnelPending} blocked {blocked}"+(failure is null?"":" · "+failure);
        _effectObjectVisualStatus+=" · "+_warpTunnelStatus;
    }
    private sealed class TunnelParticle(EnbCurrentRetailRObjParticleAsset asset,uint clock)
    {
        public EnbParticleBufferDetailBudgetSemantics.Owner? DetailOwner;
        public readonly EnbCurrentRetailRObjParticleAsset Asset=asset;
        public EnbRetailParticleSceneLifetime Buffer{get;}=new(asset.Definition,clock);
        public Matrix4x4 World=Matrix4x4.Identity;
        public uint? LastAge,RemovalClock;
        public bool Retired,SceneVisible;
        public string? Diagnostic;
        public readonly uint Capacity=EnbRetailParticleEmissionArithmetic.Capacity(asset.Definition);
        public readonly int LevelCount=EnbParticleBufferDetailBudgetSemantics.ResolveCopiedLevelCount(EnbRetailParticleEmissionArithmetic.Capacity(asset.Definition));
        public int Level=EnbParticleBufferDetailBudgetSemantics.ResolveCopiedLevelCount(EnbRetailParticleEmissionArithmetic.Capacity(asset.Definition))-1;
        public readonly float MaximumSize=EnbRetailParticleBufferBounds.MaximumSize(asset.Definition.Size,EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(asset.Definition.LifetimeSeconds),1);
        public float ProjectedDetail;
        public EnbW3dWorldBounds Bounds;
    }
    private sealed class WarpTunnelInstance(EnbCurrentRetailEffectOwner node,uint mapperStart)
    {
        public EnbCurrentRetailEffectOwner Node{get;}=node;
        public uint MapperStart{get;}=mapperStart;
        public EnbWarpTunnelFade Fade{get;}=new();
        public IEnbRenderObject? Owner;
        public bool Live,Active,SceneVisible;
        public Matrix4x4 World;
        public EnbWarpTunnelAsset? Asset;
        public EnbW3dRenderResource? SceneResource;
        public TunnelParticle[] Children=[];
    }
}
