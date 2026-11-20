using EnbNext.Engine;

namespace EnbNext.Net7.Tests;

public sealed class EnbUiThreadContextTests
{
    [Fact]
    public void PostedCallbacksAreFifoAndRecursiveWorkWaitsForNextTurn()
    {
        var context=new EnbUiThreadContext();var calls=new List<int>();
        context.Post(_=>{calls.Add(1);context.Post(_=>calls.Add(3),null);},null);
        context.Post(_=>calls.Add(2),null);
        context.Drain();Assert.Equal(new[]{1,2},calls);Assert.Equal(1,context.PendingCallbacks);
        context.Drain();Assert.Equal(new[]{1,2,3},calls);Assert.Equal(0,context.PendingCallbacks);
        Assert.Same(context,context.CreateCopy());
    }
    [Fact]
    public void GutterRequestCanAwaitAWorkerResponseWithoutBlockingTheOwner()
    {
        var context=new EnbUiThreadContext();var previous=SynchronizationContext.Current;
        var response=new TaskCompletionSource<bool>();int resumed=0;uint requested=0;
        var gutter=new EnbGutterUi((id,_)=>{requested=id;return response.Task;},()=>null);
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            async Task Click(){await gutter.RequestTargetAsync(42);resumed=Environment.CurrentManagedThreadId;}
            context.Observe(Click());Assert.Equal(42u,requested);Assert.Equal(1,context.PendingOperations);
            context.Drain();Assert.Equal(0,resumed);
            bool eventServiced=false;context.Post(_=>eventServiced=true,null);context.Drain();Assert.True(eventServiced);
            OnWorker(()=>response.SetResult(true));
            // The worker only publishes a continuation. UI state remains untouched.
            Assert.Equal(0,resumed);context.Drain();
            Assert.Equal(Environment.CurrentManagedThreadId,resumed);Assert.Equal(0,context.PendingOperations);
            Assert.Equal(0,context.PendingCallbacks);
        }
        finally {SynchronizationContext.SetSynchronizationContext(previous);}
    }
    [Fact]
    public void FailedAsyncEventsReachThePumpAndRestoreItsCallerContext()
    {
        var context=new EnbUiThreadContext();var previous=SynchronizationContext.Current;
        var error=new IOException("fixture failure");context.Observe(Task.FromException(error));
        Assert.Equal(0,context.PendingOperations);Assert.Same(previous,SynchronizationContext.Current);
        Assert.Same(error,Assert.Throws<IOException>(()=>context.Drain()));
        Assert.Same(previous,SynchronizationContext.Current);Assert.Equal(0,context.PendingCallbacks);
    }
    [Fact]
    public void ForeignThreadsCannotDrainOrSynchronouslyExecuteUiWork()
    {
        var context=new EnbUiThreadContext();bool ran=false;
        OnWorker(()=>{
            Assert.Throws<InvalidOperationException>(()=>context.Drain());
            Assert.Throws<InvalidOperationException>(()=>context.Send(_=>ran=true,null));
            context.Post(_=>ran=true,null);
        });
        Assert.False(ran);context.Drain();Assert.True(ran);
    }
    [Fact]
    public void CallbackFailureDoesNotDiscardLaterQueuedWork()
    {
        var context=new EnbUiThreadContext();bool ran=false;
        context.Post(_=>throw new IOException(),null);context.Post(_=>ran=true,null);
        Assert.Throws<IOException>(()=>context.Drain());Assert.False(ran);
        context.Drain();Assert.True(ran);
    }
    private static void OnWorker(Action work)
    {
        Exception? error=null;
        var thread=new Thread(()=>{try{work();}catch(Exception ex){error=ex;}});
        thread.Start();thread.Join();
        if(error is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
