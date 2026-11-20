using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private double[] _lastRslRefreshStages=[];
    private readonly EnbCurrentRetailSceneCallbackRegistry _particleCallbacks=new();
    private readonly List<Action> _particleCallbackFinishes=[];
    private Dictionary<CompositeParticle,string> _compositeCallbackFailures=[];
    private HashSet<CompositeParticle> _compositeCallbackRetiring=[];

    private void PrepareSharedParticleCallbacks(LiveFrame frame,RootTaskList roots,
        IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,EnbRetailAssetBundle assets)
    {
        if(_shipExhaustOwner is not null)return;
        _particleCallbackFinishes.Clear();
        uint clock=frame.CurrentClientTime;
        using var stages=FullClientBenchmarkMeter.BeginStages(FullClientBenchmarkMeter.WorldStagesEnabled && _useMainViewEnvironment && !_embeddedScanner,"callback-phase.retained-prepare");
        UpdateRetainedEmitters(clock,roots);
        stages?.Next("callback-phase.exhaust-prepare");
        AdvanceExhaustCallbacks(clock);
        stages?.Next("callback-phase.attachment-prepare");
        AdvanceAttachmentCallbacks(clock);
        stages?.Next("callback-phase.composite-prepare");
        var active=nodes.Where(n=>n.Active).Select(n=>(n.Root.EffectId,n.Root.InstanceGeneration,n.Sequence)).ToHashSet();
        _compositeCallbackFailures=UpdateCompositeCallbacks(active,clock,out _compositeCallbackRetiring);
        stages?.Next("callback-phase.jets-prepare");
        PrepareParticleJets(nodes,frame,assets);
        stages?.Next("callback-phase.tunnel-prepare");
        AdvanceTunnelCallbacks(clock);
        //9661C0 uses one mutable registration list. Newly prepended buffers
        //wait for the next walk; removal/reuse can still change the live Next.
        stages?.Next("callback-phase.dispatch-and-finish");
        DispatchParticleCallbacks();
    }
    private void DispatchParticleCallbacks()
    {
        _particleCallbacks.Dispatch();
        foreach(var finish in _particleCallbackFinishes)finish();
        _particleCallbackFinishes.Clear();
    }
}
