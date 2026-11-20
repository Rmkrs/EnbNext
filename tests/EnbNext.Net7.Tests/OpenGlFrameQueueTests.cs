using EnbNext.Rendering.OpenGL;
using Xunit;
namespace EnbNext.Net7.Tests;
public class OpenGlFrameQueueTests
{
    [Fact] public void LimitsTwoFramesAndSharesNestedPasses()
    {
        var events = new List<string>(); int next = 0;
        var queue = new EnbOpenGlFrameQueue.Queue(() => { events.Add("fence"); return ++next; }, s => { events.Add("wait" + s); return 0x911A; }, s => events.Add("delete" + s), () => events.Add("flush"));
        queue.Begin(); queue.Begin(); queue.End(); Assert.Empty(events); queue.End();
        queue.Begin(); queue.End();
        Assert.Equal(new[] {"fence", "flush", "fence", "flush"}, events);
        queue.Begin(); queue.End(); queue.Release(false);
        Assert.Equal(new[] {"fence", "flush", "fence", "flush", "wait1", "delete1", "fence", "flush", "delete3", "delete2"}, events);
    }
    [Theory] [InlineData(0x911B)] [InlineData(0x911D)]
    public void FailedWaitNeverSubmitsThirdFrame(uint result)
    {
        int fences = 0, waits = 0;
        var queue = new EnbOpenGlFrameQueue.Queue(() => ++fences, _ => { waits++; return result; }, _ => { }, () => { });
        queue.Begin(); queue.End(); queue.Begin(); queue.End();
        Assert.Throws<InvalidOperationException>(() => queue.Begin());
        Assert.Throws<InvalidOperationException>(() => queue.Begin());
        Assert.Equal(2, fences); Assert.Equal(1, waits);
    }
    [Fact] public void ProductionTimeoutRetainsFencesAndRetriesWithoutThirdSubmission()
    {
        int fences=0,waits=0;var deleted=new List<nint>();
        var queue=new EnbOpenGlFrameQueue.Queue(()=>++fences,_=>++waits<3?0x911Bu:0x911Cu,s=>deleted.Add(s),()=>{});
        Assert.True(queue.TryBegin());queue.End();Assert.True(queue.TryBegin());queue.End();
        Assert.False(queue.TryBegin());Assert.False(queue.TryBegin());
        Assert.Equal(2,fences);Assert.Empty(deleted);
        Assert.True(queue.TryBegin());Assert.Equal(new nint[]{1},deleted);
        queue.End();Assert.Equal(3,fences);
        queue.Release(false);Assert.Equal(3,deleted.Distinct().Count());
    }
    [Fact] public void ProductionWaitFailureStillPoisonsQueue()
    {
        int fences=0;
        var queue=new EnbOpenGlFrameQueue.Queue(()=>++fences,_=>0x911D,_=>{},()=>{});
        queue.Begin();queue.End();queue.Begin();queue.End();
        Assert.Throws<InvalidOperationException>(()=>queue.TryBegin());
        Assert.Throws<InvalidOperationException>(()=>queue.TryBegin());Assert.Equal(2,fences);
    }
    [Fact] public void LostContextDiscardsWithoutCallingDriver()
    {
        int deletes = 0;
        var queue = new EnbOpenGlFrameQueue.Queue(() => 1, _ => 0x911C, _ => deletes++, () => { });
        queue.Begin(); queue.End(); queue.Release(true); queue.Release(false);
        Assert.Equal(0, deletes);
    }
    [Fact] public void FailedFenceStopsFurtherSubmission()
    {
        var queue = new EnbOpenGlFrameQueue.Queue(() => 0, _ => 0, _ => { }, () => throw new Exception());
        queue.Begin(); Assert.Throws<InvalidOperationException>(() => queue.End());
        Assert.Throws<InvalidOperationException>(() => queue.Begin());
    }
}
