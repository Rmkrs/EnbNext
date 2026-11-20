namespace EnbNext.Rendering;

/// <summary>Invocation-owned task storage, drained only after every deferred pass.
/// Nested views use separate arenas. Explicit historical consumers retain a task;
/// ordinary temporary tasks release all scene references at drain.</summary>
public sealed class EnbW3dDrawTaskArena
{
    [ThreadStatic] internal static EnbW3dDrawTaskArena? Current;
    private readonly Stack<EnbW3dDrawTask> available=[];
    private readonly List<EnbW3dDrawTask> active=[];
    private readonly Stack<EnbW3dDrawTask.FrameStorage> availableFrames=[];
    private readonly List<EnbW3dDrawTask.FrameStorage> activeFrames=[];
    internal EnbW3dDrawTask.FrameStorage RentFrame(EnbW3dDrawTask.FrameStorage source)
    {
        var frame=availableFrames.TryPop(out var slot)?slot:new(new EnbW3dRigidFrameInputs());
        frame.Retained=false;frame.CopyFrom(source);activeFrames.Add(frame);return frame;
    }
    private readonly Stack<EnbCurrentRetailMeshInvocation> availableInvocations=[];
    private readonly List<EnbCurrentRetailMeshInvocation> activeInvocations=[];
    internal EnbCurrentRetailMeshInvocation RentInvocation(EnbW3dDrawTask geometry,EnbCurrentRetailMeshAppearance appearance)
    {
        var invocation=availableInvocations.TryPop(out var slot)?slot:new(geometry,appearance);
        invocation.Reuse(geometry,appearance);activeInvocations.Add(invocation);return invocation;
    }
    private readonly Stack<EnbW3dDrawInvocation> availableDraws=[];
    private readonly List<EnbW3dDrawInvocation> activeDraws=[];
    internal EnbW3dDrawInvocation RentDraw()
    {
        var draw=availableDraws.TryPop(out var slot)?slot:new();
        activeDraws.Add(draw);return draw;
    }
    private EnbW3dDrawTaskArena? previous;
    private bool entered;
    internal long Invocation {get;private set;}
    public Scope Enter()
    {
        if(entered)throw new InvalidOperationException("Task arena is already active.");
        Invocation++;previous=Current;Current=this;entered=true;return new(this);
    }
    internal EnbW3dDrawTask Rent(EnbW3dDrawTask source)
    {
        // Temporarily bypass the arena only when its high-water storage grows.
        EnbW3dDrawTask task;
        if(available.TryPop(out var slot))task=slot.ReuseFrom(source);
        else {var current=Current;Current=null;try{task=source.ForkTask();}finally{Current=current;}}
        active.Add(task);return task;
    }
    private void Drain()
    {
        if(!entered||!ReferenceEquals(Current,this))throw new InvalidOperationException("Task scopes must drain in reverse order.");
        Current=previous;previous=null;entered=false;
        // Cache/history retainers recursively retain invocation.Geometry. Keep
        // such identities detached from the pool until their consumer retires.
        foreach(var draw in activeDraws)
            if(!draw.Retained){draw.ClearAfterDrain();availableDraws.Push(draw);}
        activeDraws.Clear();
        foreach(var invocation in activeInvocations)
            if(!invocation.Geometry.RetainedBeyondInvocation)
            {invocation.ClearAfterDrain();availableInvocations.Push(invocation);}
        activeInvocations.Clear();
        foreach(var task in active)
            if(!task.RetainedBeyondInvocation){task.ClearAfterDrain();available.Push(task);}
        active.Clear();
        foreach(var frame in activeFrames)
            if(!frame.Retained){frame.Reset();availableFrames.Push(frame);}
        activeFrames.Clear();
    }
    public readonly struct Scope : IDisposable
    {
        private readonly EnbW3dDrawTaskArena arena;
        internal Scope(EnbW3dDrawTaskArena arena)=>this.arena=arena;
        public void Dispose()=>arena.Drain();
    }
}
