using EnbNext.Assets;
namespace EnbNext.Rendering;

public sealed partial class EnbCurrentRetailEffectRslObjectRuntime
{
    public readonly record struct RslCallbackCounters(int ActiveStates,int EffectiveActions,int EffectActionsApplied,
        int EffectEventsIgnored,int AnimationWeights,int DirectStateSelectionsApplied,int DirectStateSelectionsRejected,
        int FailClosedEvents,string? Diagnostic)
    {
        public EnbCurrentRetailEffectRslObjectReceipt CaptureDiagnostics()=>new(ActiveStates,EffectiveActions,EffectActionsApplied,
            EffectEventsIgnored,AnimationWeights,DirectStateSelectionsApplied,DirectStateSelectionsRejected,FailClosedEvents,Diagnostic);
        internal static RslCallbackCounters FromDiagnostic(EnbCurrentRetailEffectRslObjectReceipt value)=>new(value.ActiveStates,
            value.EffectiveActions,value.EffectActionsApplied,value.EffectEventsIgnored,value.AnimationWeights,
            value.DirectStateSelectionsApplied,value.DirectStateSelectionsRejected,value.FailClosedEvents,value.Diagnostic);
    }

    internal sealed class CallbackScratch : IDisposable
    {
        private bool active;
        private CallbackScratch? nested;
        public readonly List<string> Diagnostics=[];
        private readonly List<(EnbCurrentRetailRenderStateEffectEvent Event,int Index)> sort=[];
        private readonly List<EnbCurrentRetailRenderStateEffectEvent> ordered=[];
        public CallbackScratch Rent()
        {
            if(active)return (nested??=new()).Rent();
            active=true;return this;
        }
        public IReadOnlyList<EnbCurrentRetailRenderStateEffectEvent> OrderEvents(IReadOnlyList<EnbCurrentRetailRenderStateEffectEvent> events)
        {
            bool sorted=true;
            for(int i=1;i<events.Count;i++)if(events[i-1].Sequence>events[i].Sequence){sorted=false;break;}
            if(sorted)return events;
            for(int i=0;i<events.Count;i++)sort.Add((events[i],i));
            sort.Sort(static(a,b)=>{int order=a.Event.Sequence.CompareTo(b.Event.Sequence);return order!=0?order:a.Index.CompareTo(b.Index);});
            for(int i=0;i<sort.Count;i++)ordered.Add(sort[i].Event);
            return ordered;
        }
        public void Dispose(){Diagnostics.Clear();sort.Clear();ordered.Clear();active=false;}
    }

    private struct ActiveCallback(EnbCurrentRetailEffectRslObjectRuntime runtime,ObjectState state,
        EnbResolvedRslLibraryResourceGraph graph,uint tick,float delta,Func<int,uint,bool>? completed,
        Func<int,uint,int,bool>? completedAtActivation,List<string> diagnostics,int failures)
        : EnbCurrentRetailRslActiveListRuntime.IVisitor
    {
        public int FailClosedEvents=failures;
        public void Visit(int id)=>runtime.AdvanceActiveState(id,state,graph,tick,delta,completed,
            completedAtActivation,diagnostics,ref FailClosedEvents);
    }
}
