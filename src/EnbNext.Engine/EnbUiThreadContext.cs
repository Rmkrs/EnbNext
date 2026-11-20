using System.Collections.Concurrent;

namespace EnbNext.Engine;

/// <summary>The game thread services asynchronous UI work between events and rendering.</summary>
internal sealed class EnbUiThreadContext : SynchronizationContext
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> queue = new();
    private int operations;
    internal int PendingOperations => Volatile.Read(ref operations);
    internal int PendingCallbacks => queue.Count;
    public override SynchronizationContext CreateCopy() => this;
    public override void Post(SendOrPostCallback callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        queue.Enqueue((callback, state));
    }
    public override void Send(SendOrPostCallback callback, object? state)
    {
        VerifyOwner();
        callback(state);
    }
    public override void OperationStarted() => Interlocked.Increment(ref operations);
    public override void OperationCompleted() => Interlocked.Decrement(ref operations);
    internal void Drain()
    {
        VerifyOwner();
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            int remaining = queue.Count;
            while (remaining-- > 0 && queue.TryDequeue(out var work))
                work.Callback(work.State);
        }
        finally { SetSynchronizationContext(previous); }
    }
    internal void Observe(Task task)
    {
        VerifyOwner();
        ArgumentNullException.ThrowIfNull(task);
        var previous = Current;
        SetSynchronizationContext(this);
        try { Await(task); }
        finally { SetSynchronizationContext(previous); }
    }
    // async-void deliberately routes errors and operation lifetime through this
    // context, just as a native async event does, without blocking the event pump.
    private static async void Await(Task task) => await task;
    private void VerifyOwner()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("UI callbacks must be serviced on their owning thread.");
    }
}
