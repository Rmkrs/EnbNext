using EnbNext.Rendering.OpenGL;
using Xunit;
namespace EnbNext.Net7.Tests;
public class GlFenceWaitTests
{
    [Fact] public void PendingQuerySleepsAndCompletesWithoutFalseTimeout()
    {
        long now=0;int calls=0,sleeps=0;var flags=new List<uint>();
        uint result=EnbGlFenceWait.Wait(1,(_,f)=>{flags.Add(f);return ++calls==3?0x911Cu:0x911Bu;},()=>now,()=>{sleeps++;now+=1;},50);
        Assert.Equal(0x911Cu,result);Assert.Equal(2,sleeps);Assert.Equal(new uint[]{1,0,0},flags);
    }
    [Fact] public void DeadlineIsMeasuredEvenIfDriverReturnsImmediately()
    {
        long now=0;int sleeps=0;
        uint result=EnbGlFenceWait.Wait(1,(_,_)=>0x911Bu,()=>now,()=>{sleeps++;now+=5;},50);
        Assert.Equal(0x911Bu,result);Assert.Equal(50,now);Assert.Equal(10,sleeps);
        Assert.Equal(50,EnbGlFenceWait.LastTimeoutTicks);
        EnbGlFenceWait.Wait(1,(_,_)=>0x911Cu,()=>now,()=>{},50);
        Assert.Equal(0,EnbGlFenceWait.LastTimeoutTicks);
    }
    [Theory][InlineData(0x911A)][InlineData(0x911D)]
    public void ReadyAndFailedReturnWithoutSleeping(uint result)
        =>Assert.Equal(result,EnbGlFenceWait.Wait(1,(_,_)=>result,()=>0,()=>throw new Exception("Unexpected sleep"),50));
}
