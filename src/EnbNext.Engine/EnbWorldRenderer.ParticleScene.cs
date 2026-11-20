using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private ulong? _particleSceneVersion;

    private bool PrepareParticleScene(IEnbSceneFrame snapshot)
    {
        ulong version=snapshot.LocationTransition?.Version??0;
        bool replaced=_particleSceneVersion is {} previous && previous!=version;
        _particleSceneVersion=version;
        if(!replaced && !snapshot.IsTransitioning)return false;

        // 741BB0 -> MainView +10 (AECF88 -> 50C2F0) releases the scene.
        // 75EED0 -> 494F50 -> 965BF0 removes ALL scene RenderObjs, including
        // independently retained particle buffers. Owner destruction alone
        // leaves a draining tail and is therefore not a scene teardown.
        // Observe the persistent handoff version too: rendering can miss the
        // loading snapshot. Borrowing views never mutate their source owner.
        _particleJets.Clear();_particleJetCallbacks.Clear();_particleJetFactories.Clear();_jetBufferCollision.Clear();
        _retainedEmitterCallbacks.Clear();_retainedEmitters.Clear();_retiringEmitterBuffers.Clear();_dazzleRetainedBuffers.Clear();
        ResetPhysicalFactoryMembership();_particleSceneRemovals.Clear();_particleSceneRemovalOwners.Clear();
        _removedRetainedEmitters.Clear();ClearCompositeMembership();_retainedCompositeFrameRoots.Clear();
        _discardedParticleFactories.Clear();_retainedEmitterRslUpdates.Clear();ClearRetainedWrapperAttachmentHistory();
        _retainedEmitterErrors.Clear();
        _shipExhaustInstances.Clear();_exhaustCallbacks.Clear();_frameShipExhaust.Clear();_exhaustBufferCollision.Clear();
        _particleEmitterStartTimes.Clear();_ordinaryParticleBirths.Clear();
        _warpPlume.Clear();_effectRslScheduler.ClearAttachmentBindings();_warpPlumes.Clear();_attachedEffects.Clear();
        _attachmentCallbacks.Clear();
        _attachmentEpoch++;_attachmentBufferCollision.Clear();
        _bloodOwners.Clear();_bloodAttachments.Clear();_bloodSounds.Clear();
        _shipDamageStates.Clear();_shipDamageChildren.Clear();_shipDamageAttachments.Clear();
        ClearProjectedDecals();
        _warpTunnels.Clear();_tunnelCallbacks.Clear();_warpTunnelCollision.Clear();_warpTunnelBufferCollision.Clear();
        return true;
    }
}
