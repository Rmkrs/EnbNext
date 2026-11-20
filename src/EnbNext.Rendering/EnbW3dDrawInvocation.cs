namespace EnbNext.Rendering;

/// <summary>Mutable invocation storage owned by the active view's task arena.
/// Every traversal has a distinct slot until all deferred passes drain. Retain
/// explicitly for a definition/history that outlives that invocation.</summary>
public sealed class EnbW3dDrawInvocation
{
    public EnbW3dRenderResourceKey ResourceKey {get;private set;}
    public ulong RenderIdentity {get;private set;}
    public ulong VisualGeneration {get;private set;}
    public long FrameTimeMilliseconds {get;set;}
    public EnbW3dHlodSelection? HlodSelection {get;private set;}
    public EnbW3dAnimationSample? AnimationSample {get;private set;}
    public List<EnbW3dDrawTask> Packets {get;}=[];
    public List<EnbW3dDazzleDrawInstance> Dazzles {get;}=[];
    public List<EnbW3dParticleEmitterDrawInstance> ParticleEmitters {get;}=[];
    internal bool Retained {get;private set;}
    internal EnbW3dDrawInvocation() {}
    public EnbW3dDrawInvocation(EnbW3dRenderResourceKey key,ulong identity,ulong generation,long time,
        EnbW3dHlodSelection? selection,EnbW3dAnimationSample? animation,IEnumerable<EnbW3dDrawTask> packets)
    {Reset(key,identity,generation,time,selection,animation);Packets.AddRange(packets);}
    public static EnbW3dDrawInvocation Create(EnbW3dRenderResourceKey key,ulong identity,ulong generation,long time,
        EnbW3dHlodSelection? selection,EnbW3dAnimationSample? animation)
    {
        var value=EnbW3dDrawTaskArena.Current?.RentDraw()??new();
        value.Reset(key,identity,generation,time,selection,animation);return value;
    }
    private void Reset(EnbW3dRenderResourceKey key,ulong identity,ulong generation,long time,
        EnbW3dHlodSelection? selection,EnbW3dAnimationSample? animation)
    {ResourceKey=key;RenderIdentity=identity;VisualGeneration=generation;FrameTimeMilliseconds=time;
     HlodSelection=selection;AnimationSample=animation;Retained=false;}
    public EnbW3dDrawInvocation CopyForInvocation()
    {
        var copy=Create(ResourceKey,RenderIdentity,VisualGeneration,FrameTimeMilliseconds,HlodSelection,AnimationSample);
        copy.Packets.AddRange(Packets);copy.Dazzles.AddRange(Dazzles);copy.ParticleEmitters.AddRange(ParticleEmitters);return copy;
    }
    public void RetainAfterDrain()
    {Retained=true;foreach(var task in Packets)task.RetainAfterDrain();}
    internal void ClearAfterDrain()
    {Packets.Clear();Dazzles.Clear();ParticleEmitters.Clear();ResourceKey=default;HlodSelection=null;AnimationSample=null;}
}
