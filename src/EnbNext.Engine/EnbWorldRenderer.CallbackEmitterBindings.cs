using EnbNext.Rendering;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private long _boundCallbackEmitterRefreshes;
    private sealed record RetainedEmitterBinding(RetainedEmitterKey Key,RetainedEmitterOwner Owner);
    private bool TryRefreshBoundCallbackEmitters(long now,IReadOnlySet<(ulong Identity,ulong Generation)> live,
        IReadOnlySet<ulong> owners,CallbackRootRefreshScratch refresh)
    {
        // The current model/child graph survived its callback. Registration was
        // completed before callback dispatch. Pending assets or removals retain
        // the existing local reconstruction path; no publication is skipped.
        if(now<_retainedEmitterTime)return false;
        foreach(var hidden in _hiddenRslEmitterRoots)
            if(owners.Contains(hidden.Object.RenderIdentity))return false;
        foreach(var hidden in _hiddenCombatSourceRoots)
            if(owners.Contains(hidden.Object.RenderIdentity))return false;
        foreach(var instance in refresh.Current)
        {
            if(instance.Root.RetainedEmitter is null)continue;
            if(instance.PreparedEmitter is not {} binding || !ReferenceEquals(binding.Owner.LastInstance,instance) || !ReferenceEquals(binding.Owner.Instance,instance) ||
                binding.Owner.State.Destroyed || binding.Key.Owner!=instance.Object.RenderIdentity ||
                binding.Key.Generation!=(binding.Key.PhysicalFactory is not null?0:instance.IsRsl?instance.Object.RenderObjectGeneration:instance.Object.VisualGeneration) ||
                binding.Owner.SceneRemovalClock is not null || _removedRetainedEmitters.Contains(binding.Key) ||
                !RetainedParticleOwnerIsLive(binding.Key,live))return false;
        }
        using var scratch=_emitterPreparationScratch.Rent();
        // Resolve every dynamic pose before committing any lifetime mutation.
        // A changed scale/HSV returns to local registration, never an old key.
        foreach(var instance in refresh.Current)
        {
            if(instance.Root.RetainedEmitter is null)continue;
            var binding=instance.PreparedEmitter!;
            if(!TryRefreshHierarchyOnce(instance,scratch.Refreshed,out _) ||
                !TryResolvePrimitiveFactoryTransform(instance,out _,out float scale,out var hsv) ||
                scale!=binding.Key.Scale || hsv!=binding.Key.Hsv)return false;
        }
        foreach(var instance in refresh.Current)
        {
            if(instance.Root.RetainedEmitter is not {} asset)continue;
            var binding=instance.PreparedEmitter!;var owner=binding.Owner;
            try
            {
                if(!TryRefreshHierarchyOnce(instance,scratch.Refreshed,out var error))throw new InvalidDataException(error);
                if(!TryResolvePrimitiveFactoryTransform(instance,out var world,out _,out _))
                    throw new InvalidDataException("Retained emitter factory transform unavailable");
                BindRetainedLegoEmitterScene(binding.Key,owner,instance);
                owner.Instance=instance;owner.World=world;owner.LastVisualGeneration=instance.Object.VisualGeneration;
                owner.State.UpdateTransform(world);owner.LodBias=instance.Root.LodBias;
            }
            catch(InvalidDataException ex){owner.Instance=null;_retainedEmitterErrors.Add($"Retained emitter '{asset.Definition.Name}': {ex.Message}");}
        }
        // Retired autonomous buffers and every ordered visibility setter still
        // advance through the same lifetime consumer. Unchanged scene membership
        // needs no global factory walk, wrapper-set rebuild or retirement sweep.
        _retainedEmitterTime=now;
        PrepareCallbackEmitterLifetimes(now,live,owners);
        CommitWrapperAttachments();
        _boundCallbackEmitterRefreshes++;
        return true;
    }
}
