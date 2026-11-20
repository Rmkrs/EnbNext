using EnbNext.Engine;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbHorizontalSliderInputTests
{
    [Fact]
    public void NonzeroDomainPreservesFractionalTravelAndRepeatEndpoints()
    {
        var s=new EnbSliderInput(new(316,163,138,14),new(302,163,166,14),true,true,1);
        s.Configure(100,1);s.SetValue(50.5);
        Assert.Equal(16,s.ThumbLength);Assert.Equal(377,s.ThumbStart);
        s.Begin(EnbScrollbarPart.Thumb,new(380.2,170),0);
        s.Move(new(410.7,170));Assert.Equal(75.25,s.Value);
        s.Move(new(410.7,130));Assert.Equal(50.5,s.Value);
        s.Cancel();s.SetValue(1);s.Begin(EnbScrollbarPart.Up,new(310,170),0);
        Assert.Equal(1,s.Value);s.Advance(new(310,170),500);Assert.Equal(1,s.Value);
        s.Cancel();s.SetValue(100);s.Begin(EnbScrollbarPart.Down,new(460,170),0);
        Assert.Equal(100,s.Value);s.Configure(1,1);Assert.Equal(1,s.Value);Assert.Equal(EnbScrollbarPart.None,s.Pressed);
    }

    [Fact]
    public void HorizontalEdgesRepeatAndCancellationUseTheNativeAxis()
    {
        var s = new EnbSliderInput(new(20, 100, 200, 16), new(4, 100, 232, 16), true);
        s.Configure(41, 11); s.SetValue(7.25);
        Assert.Equal(EnbScrollbarPart.Up, s.Hit(new(4, 100)));
        Assert.Equal(EnbScrollbarPart.Down, s.Hit(new(236, 116)));
        Assert.Equal(EnbScrollbarPart.None, s.Hit(new(237, 108)));
        double grab = s.ThumbStart + 5;
        s.Begin(EnbScrollbarPart.Thumb, new(grab, 108), 1000);
        s.Move(new(grab + 20, 108)); Assert.True(s.Value > 7.25);
        s.Move(new(grab, 67.9)); Assert.Equal(7.25, s.Value);
        s.Move(new(grab + 20, 68)); Assert.True(s.Value > 7.25);
        s.Cancel(); Assert.False(s.Move(new(grab, 108)));
        s.SetValue(0); s.Begin(EnbScrollbarPart.Down, new(228, 108), 1000);
        Assert.Equal(1, s.Value); Assert.False(s.Advance(new(228,108),1499));
        Assert.False(s.Advance(new(8,108),1500)); Assert.Equal(1500,s.RepeatAt);
        Assert.True(s.Advance(new(228,108),1600)); Assert.Equal(2,s.Value);
        Assert.Equal(1600,s.RepeatAt);
        s.Configure(1,1); Assert.Equal(EnbScrollbarPart.None,s.Pressed);
    }

    [Fact]
    public void BothAxesHaveIdenticalTransposedTrajectoriesWithoutWarmAllocation()
    {
        var v = new EnbSliderInput(new(100,20,16,200),new(100,4,16,232));
        var h = new EnbSliderInput(new(20,100,200,16),new(4,100,232,16),true);
        for (int i=0;i<1024;i++)
        {
            v.Configure(41,11);h.Configure(41,11);v.SetValue(i%30+.25);h.SetValue(i%30+.25);
            var p = new EnbUiPoint(108,4+i%233);var q = new EnbUiPoint(p.Y,p.X);
            Assert.Equal(v.Hit(p),h.Hit(q));
            v.Begin(v.Hit(p),p,1000);h.Begin(h.Hit(q),q,1000);
            v.Move(new(p.X,p.Y+20));h.Move(new(q.X+20,q.Y));
            v.Advance(p,1500);h.Advance(q,1500);
            Assert.Equal(v.Value,h.Value);Assert.Equal(v.ThumbStart,h.ThumbStart);
            Assert.Equal(v.ThumbLength,h.ThumbLength);Assert.Equal(v.NotchChanged,h.NotchChanged);
            v.Cancel();h.Cancel();
        }
        var right = new EnbUiPoint(228,108);
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<4096;i++) { h.Configure(41,11);h.SetValue(0);h.Begin(EnbScrollbarPart.Down,right,0);h.Advance(right,500);h.Cancel(); }
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);
    }
}
