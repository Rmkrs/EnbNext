using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>A prepared Animation-only NEXT graph. This clock does not
/// admit an effect asset or mutate object, sound, particle or renderer state.</summary>
public sealed class EnbCurrentRetailRslAnimationSequenceRuntime
{
    private sealed record Step(int StateId,EnbRslStateFlags Flags,EnbW3dRenderHierarchyAnimation Animation,int? NextStateId);
    public readonly record struct Sample(int StateId,EnbW3dRenderHierarchyAnimation Animation,float Frame,uint ActivationTick)
    {
        public bool Active {get;init;}=true;
        public EnbW3dRenderHierarchyAnimation? BoundAnimation => Active?Animation:null;
    }
    private readonly Step[] _steps;
    private readonly Dictionary<int,int> _indices;
    private readonly HashSet<int> _activated=[];
    private int _index;
    private float _frame;
    private bool _active=true;
    private uint _activationTick;
    private uint _lastTick;
    private Sample? _lastSample;

    public EnbCurrentRetailRslAnimationSequenceRuntime(EnbResolvedRslLibraryResourceGraph graph,
        int initialStateId,IReadOnlyDictionary<int,EnbW3dRenderHierarchyAnimation> clips,uint activationTick)
    {
        var steps=new List<Step>();
        var seen=new HashSet<int>();
        int id=initialStateId;
        while(true)
        {
            if(!seen.Add(id))break;
            if(!graph.TryFindState(id,out var state) || state.Definition.Type!=EnbRslStateType.Animation ||
                !clips.TryGetValue(id,out var animation))
                throw new InvalidDataException($"Animation sequence state {id} or its prepared clip is unavailable");
            var d=state.Definition;var f=d.Flags;
            //45AD90 does not read the other classes' DURATION/INITSTR/MESSAGE.
            if(d.LinkStateId is not null ||
                f.Delete || f.Enabled || f.StartsEnabled ||
                animation.NumFrames==0 || animation.PlaybackFrameRate==0)
                throw new InvalidDataException($"Animation sequence state {id} has unsupported control or clip data");
            //45AD90 parses PIVOTMAP but leaves931350's mask pointer null;
            //4596A0/4597E0 copy that unmasked blend record through9313B0.
            //457BE0 also clears RemovePrototype/Stationary: do not reject
            //those inert authored bits before applying the native copy mask.
            f=EnbCurrentRetailRslAnimationRuntime.CopyTemplateFlags(f);
            steps.Add(new(id,f,animation,d.NextStateId));
            if(!f.ActivateNext)
            {
                //0045A7E0 retains or clones the target; Animation's004598B0/
                //004597E0 only copy state.004584E7 gates target callbacks on
                //0x28, so an inactive Animation target never binds its HAnim.
                if(d.NextStateId is int inactive &&
                    (!graph.TryFindState(inactive,out var targetState) || targetState.Definition.Type!=EnbRslStateType.Animation))
                    throw new InvalidDataException("Inactive NEXT target is outside the animation sequence family");
                break;
            }
            var plan=EnbCurrentRetailRslAnimationLifetimeRuntime.ResolveAuthoredNext(graph,id);
            if(!plan.IsExecutable || plan.DeactivatedStateIds.Count!=1 ||
                plan.ActivatedTargetStateIds.Count!=(plan.TargetStateId is null?0:1))
                throw new InvalidDataException(plan.Diagnostic??"Animation sequence requires one source and at most one NEXT target");
            if(plan.TargetStateId is not int target)break;
            id=target;
        }
        _steps=steps.ToArray();_indices=steps.Select((step,index)=>(step.StateId,index)).ToDictionary(p=>p.StateId,p=>p.index);
        _activated.Add(initialStateId);_activationTick=activationTick;_lastTick=activationTick;
    }

    public Sample Advance(uint tick)
    {
        // Preserve uint clock wrap, but reject a backwards observation.
        if(unchecked(tick-_lastTick)>int.MaxValue)throw new InvalidDataException("Animation sequence clock moved backwards");
        if((tick==_lastTick || !_active) && _lastSample is {} previous)
        {
            _lastTick=tick;return previous;
        }
        var step=_steps[_index];
        float delta=(float)((double)unchecked(tick-_lastTick)*BitConverter.Int32BitsToSingle(0x3a83126f));
        var advance=EnbCurrentRetailRslAnimationFrameRuntime.Advance(_frame,step.Animation.PlaybackFrameRate,step.Animation.NumFrames,delta);
        uint flags=EnbCurrentRetailRslAnimationRuntime.EncodeAuthoredFlags(step.Flags)|0x10u;
        bool activateNext=step.Flags.ActivateNext;
        if(advance.TerminalCallback && activateNext && step.NextStateId is not null)
        {
            //004584B0 deactivates source, invokes target +34, then +14.
            //A cached HAnim consumes the spilled leftover; a fresh state has
            //no HAnim yet and retains its constructor frame zero.
            int targetIndex=_indices[step.NextStateId!.Value];var target=_steps[targetIndex];
            float entry=_activated.Contains(target.StateId)
                ?(float)((double)(float)target.Animation.PlaybackFrameRate*advance.LeftoverSeconds):0;
            //For self-NEXT the source's post-callback publication overwrites
            //that transferred frame after the same state has been reenabled.
            _frame=targetIndex==_index?EnbCurrentRetailRslAnimationFrameRuntime.Publish(advance,flags):entry;
            _index=targetIndex;step=target;_activated.Add(target.StateId);_activationTick=tick;
            //The active-list walker follows the removed source's cleared next
            //pointer, so even an overshoot never catches up through this graph.
        }
        else if(advance.TerminalCallback && (step.Flags.Deactivate || activateNext))
        {
            //00459FA0 removes the state and clears owner pose-valid bit2.
            //00460E80's empty gather resets +A0 ->0098E5D0 ->009C58E0;
            //mode1 subsequently calls009895B0 for authored bind transforms.
            _active=false;_frame=0;
        }
        else _frame=EnbCurrentRetailRslAnimationFrameRuntime.Publish(advance,flags);
        var sample=new Sample(step.StateId,step.Animation,_frame,_activationTick){Active=_active};
        _lastTick=tick;_lastSample=sample;
        return sample;
    }
}
