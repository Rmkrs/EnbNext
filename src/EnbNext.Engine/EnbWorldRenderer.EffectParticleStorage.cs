namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private long _retainedEffectParticlePackets;
    private bool RetainEffectParticleStorage=>_retainedOrdinaryMeshes && !_immutableParticleStreams && _particleStreamFrame is not null;
}
