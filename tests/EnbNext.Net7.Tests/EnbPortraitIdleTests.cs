using EnbNext.Assets;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;
public sealed class EnbPortraitIdleTests
{
    private static EnbRetailAvatarIdle Actor(string header="")=>new(EnbRetailIniDocument.Parse("[HEADER]\n"+header),(l,h)=>0);
    [Fact] public void ActivityRestartsFidgetSequenceWithoutRedrawingJitterOrBreath()
    {
        var a=Actor("FidgetDelay=2");a.Next((l,h)=>h);
        a.Advance(3,true);Assert.Equal("bored4",a.Next((l,h)=>h));
        a.Advance(1,true);int jitter=a.Jitter,breath=a.Breath;
        a.ResetForActivity();
        Assert.Equal(0,a.Elapsed);Assert.Equal(0,a.BoredCount);
        Assert.Equal(jitter,a.Jitter);Assert.Equal(breath,a.Breath);
    }
    [Fact] public void AuthoredDelayStrictThresholdAndFifthBored()
    {
        var a=Actor("FidgetDelay=2"); Assert.Equal("breathe1",a.Next((l,h)=>h));
        a.Advance(2,true); Assert.Equal("breathe4",a.Next((l,h)=>h));
        a.Advance(.01f,true);
        for(int i=0;i<5;i++)
        {
            Assert.Equal(i==4?"bored":"bored4",a.Next((l,h)=>h));
            Assert.True(a.ResetHeadTargets); Assert.Equal(1,a.Jitter); Assert.Equal(0,a.Elapsed);
            a.Advance(3.01f,true);
        }
        Assert.Equal(0,a.BoredCount);
    }
    [Fact] public void BreathSelectionReturnsToOneAndUsesInclusiveThreshold()
    {
        var a=Actor(); Assert.Equal("breathe1",a.Next((l,h)=>h));
        Assert.Equal("breathe1",a.Next((l,h)=>699));
        var samples=new Queue<int>([700,699]); Assert.Equal("breathe2",a.Next((l,h)=>samples.Dequeue()));
        Assert.Equal("breathe1",a.Next((l,h)=>throw new Exception("Must not draw")));
        samples=new([700,700,699]); Assert.Equal("breathe3",a.Next((l,h)=>samples.Dequeue()));
    }
    [Fact] public void ConstructionFlagsPostureAndSpeechGateFidgetClock()
    {
        var a=Actor("FigetDelay=1"); Assert.Equal(45,a.FidgetDelay);
        a.Advance(10,false); Assert.Equal(0,a.Elapsed);
        a.HeadMotionEnabled=false;a.Advance(10,true);Assert.Equal(0,a.Elapsed);
        a.HeadMotionEnabled=true;a.Posture=1;a.Advance(10,true);Assert.Equal(0,a.Elapsed);
        Assert.Equal("sit_breathe",a.Next((l,h)=>h));
        a.Posture=0;a.Advance(10,true);Assert.Equal(10,a.Elapsed);
        a.SuppressFidgets=true;a.Advance(1,true);Assert.Equal(0,a.Elapsed);
        a.StandStill=true;Assert.Equal("stand_still",a.Next((l,h)=>h));
    }
}
