using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly List<(AttachmentParticle,EnbRenderDetailOwner)> _detailAttachmentParticles=[];
    private readonly List<EnbDazzleCollisionRoot> _attachmentBufferCollision=[];
    private readonly EnbCurrentRetailSceneCallbackList<(AttachmentParticle Particle,bool Buffer)> _attachmentCallbacks;
    private void TrackAttachmentCallbacks(AttachmentParticle particle)
    {
        particle.State.SceneAttachmentChanged+=attached=>
        {
            if(attached)_attachmentCallbacks.Register((particle,false));
            else _attachmentCallbacks.Remove((particle,false));
        };
    }
    private void RemoveAttachmentCallbacks(AttachmentParticle particle)
    {
        _attachmentCallbacks.Remove((particle,false));_attachmentCallbacks.Remove((particle,true));
    }
    private void AdvanceAttachmentCallbacks(uint clock)
    {
        //6E4D00/98E4B0 enter cloned hierarchy children into the same retained
        //scene list;97DEF0 attaches each buffer separately during the walk.
        _attachmentCallbacks.SetCallback(entry=>
        {
            var particle=entry.Particle;var state=particle.State;
            if(particle.Diagnostic is not null)return;
            try
            {
                if(entry.Buffer)
                {
                    state.BufferCallback(clock,particle.LastClock is {} last?unchecked(clock-last):0,state.Emitter.CurrentWorld,particle.Scale);
                    if(state.CanRetire)QueueParticleSceneRemoval(state,true,()=>
                    {particle.Retired=true;RemoveAttachmentCallbacks(particle);});
                }
                else if(state.EmitterCallback(state.Emitter.CurrentWorld))_attachmentCallbacks.Register((particle,true));
            }
            catch(InvalidDataException error){particle.Diagnostic=error.Message;}
        });
        _particleCallbackFinishes.Add(()=>
        {
        foreach(var particle in _warpPlumes.Values.SelectMany(p=>p.Particles).Concat(_attachedEffects.Values.SelectMany(p=>p.Particles)))
        {
            if(particle.Retired)continue;
            particle.LastClock=clock;
            if(particle.State.Destroyed&&!particle.State.BufferAttached)
            {particle.Retired=true;RemoveAttachmentCallbacks(particle);}
        }
        });
    }
    private sealed class AttachmentParticle(EnbCurrentRetailShipExhaustAsset asset,uint clock,float scale)
    {
        public EnbParticleBufferDetailBudgetSemantics.Owner? DetailOwner;
        public readonly EnbCurrentRetailShipExhaustAsset Asset=asset;
        public readonly float Scale=scale;
        public readonly EnbRetailParticleSceneLifetime State=new(asset.Definition,clock);
        public uint? LastClock;
        public bool Retired,SceneVisible;
        public readonly uint Capacity=EnbRetailParticleEmissionArithmetic.Capacity(asset.Definition);
        public readonly int LevelCount=EnbParticleBufferDetailBudgetSemantics.ResolveCopiedLevelCount(EnbRetailParticleEmissionArithmetic.Capacity(asset.Definition));
        public int Level=EnbParticleBufferDetailBudgetSemantics.ResolveCopiedLevelCount(EnbRetailParticleEmissionArithmetic.Capacity(asset.Definition))-1;
        public readonly float MaximumSize=EnbRetailParticleBufferBounds.MaximumSize(asset.Definition.Size,EnbRetailParticleEmissionArithmetic.LifetimeMilliseconds(asset.Definition.LifetimeSeconds),scale);
        public float ProjectedDetail;
        public string? Diagnostic;
        public EnbW3dWorldBounds Bounds;
    }
    private static EnbW3dParticleEmitterDrawInstance AttachmentEmitter(AttachmentParticle particle,uint gameId,int index)
    {
        var asset=particle.Asset;
        return new(asset.Definition.Name,asset.Key.ResourceName,gameId,EnbRenderPassKind.OrdinaryWorld,
            asset.Definition,asset.Texture,particle.State.Emitter.CurrentWorld,"",0,index)
            {RuntimeTextureName=asset.RuntimeTextureName,RetainedTexture=asset.RetainedTexture};
    }
    private static EnbW3dWorldBounds AttachmentBufferBounds(AttachmentParticle particle,uint clock)
    {
        var state=particle.State;
        if(!state.BoundsDirty)return particle.Bounds;
        state.EnsureBounds(clock);
        if(!EnbCurrentRetailParticleEmitterRuntime.TryComputeBufferBounds(AttachmentEmitter(particle,0,0),state.BorrowLiveBirths(),0,clock,
            particle.Scale,state.MotionHistory,particle.MaximumSize,out var bounds,out var error))throw new InvalidDataException(error);
        return particle.Bounds=bounds
            .WorldSphere(particle.Asset.Definition.LocalSpace?state.Emitter.CurrentWorld:Matrix4x4.Identity);
    }
    private List<(AttachmentParticle Owner,EnbRenderDetailOwner Candidate)> PrepareAttachmentParticleCandidates(
        EnbResolvedRenderCamera camera,uint clock,IEnbSceneFrame snapshot)
    {
        var result=_detailAttachmentParticles;result.Clear();
        if(_shipExhaustOwner is not null)return result;
        var source=_shipExhaustOwner??this;
        var groups=source._warpPlumes.Select(p=>(p.Key.Owner,p.Key.Generation,Path:$"warp-plume/{p.Key}",p.Value.Particles))
            .Concat(source._attachedEffects.Select(p=>(p.Key.Owner,Generation:p.Key.OwnerGeneration,Path:$"attach/{p.Key}",p.Value.Particles)));
        foreach(var group in groups)
        {
            //The buffer remains in the scene independently of the captured
            //parent's composition generation, including detached living tails.
            bool included=_shipExhaustOwner is null || SceneObjects(snapshot).Any(o=>o.RenderIdentity==group.Owner);
            for(int i=0;i<group.Particles.Length;i++)
            {
                var particle=group.Particles[i];particle.SceneVisible=false;
                if(!included || particle.Retired || !particle.State.BufferAttached)continue;
                if(particle.Diagnostic is not null)continue;
                try
                {
                    var bounds=AttachmentBufferBounds(particle,clock);var definition=particle.Asset.Definition;
                    if(definition.RenderMode is not (3 or 4) && FrustumCullingEnabled && !EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(camera,bounds))continue;
                    particle.SceneVisible=true;
                    if(!EnbParticleBufferDetailBudgetSemantics.TryComputeProjectedDetail(camera,bounds,particle.Capacity,particle.MaximumSize,particle.ProjectedDetail,out var detail))continue;
                    particle.ProjectedDetail=detail.Smoothed;
                    result.Add((particle,(particle.DetailOwner ??= new(group.Path+"/"+i,particle.Capacity,(int)definition.RenderMode,particle.LevelCount)).Update(particle.Level,particle.ProjectedDetail,1f)));
                }
                catch(InvalidDataException error){particle.Diagnostic=error.Message;}
            }
        }
        return result;
    }
    private bool DrawAttachmentParticle(List<EnbW3dDrawTask> packets,AttachmentParticle particle,IEnbRenderObject owner,
        string path,int index,LiveFrame frame,EnbW3dLightEnvironment lights)
    {
        if(particle.Retired)return false;
        if(particle.Diagnostic is {} diagnostic)throw new InvalidDataException(diagnostic);
        var state=particle.State;uint clock=frame.CurrentClientTime;
        if(!state.BufferAttached || !particle.SceneVisible && _shipExhaustOwner is null)return false;
        _attachmentBufferCollision.Add(new(default,true,false,static(start,end,fraction,inside)=>(fraction,inside))
        {ResolveSphere=()=>
        {
            try {var bounds=AttachmentBufferBounds(particle,clock);return new Vector4(bounds.Center,bounds.Radius);}
            catch(InvalidDataException error){particle.Diagnostic=error.Message;return null;}
        }});
        state.AdvanceForRender(clock);
        if(!EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(particle.Asset.Key,owner.RenderIdentity,owner.VisualGeneration,
            AttachmentEmitter(particle,owner.GameId,index),frame.Camera,lights,ComputeStableParticleEmitterSalt(path),0,clock,
            out var packet,out _,out var error,particleSizeScale:particle.Scale,worldBirths:state.BorrowLiveBirths(),
            activeEmissionRun:state.Emitter.HasLineEndpoint?state.Emitter.EmissionRun:null,motionHistory:state.MotionHistory,
            internalDetailLevel:particle.LevelCount-1-particle.Level,cullToCamera:FrustumCullingEnabled))throw new InvalidDataException(error);
        if(packet is null)return false;
        packets.Add((packet).ForkTask().SetRenderInstancePath(path));return true;
    }
}
