using EnbNext.Engine;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbVerticalScrollbarInputTests
{
    private static EnbSliderInput Create(int total = 41)
    {
        var slider = new EnbSliderInput(new(100, 20, 16, 200), new(100, 4, 16, 232));
        slider.Configure(total, 11); return slider;
    }

    [Fact]
    public void ValueClampsWhileRowsTruncateAndSoundNotchesRound()
    {
        var s = Create();
        s.SetValue(.49); Assert.Equal(0, s.Offset); Assert.False(s.NotchChanged);
        s.SetValue(.9); Assert.Equal(0, s.Offset); Assert.True(s.NotchChanged);
        s.SetValue(1.1); Assert.Equal(1, s.Offset); Assert.False(s.NotchChanged);
        s.SetValue(1.9); Assert.Equal(1, s.Offset); Assert.True(s.NotchChanged);
        s.SetValue(100); Assert.Equal(30, s.Value);
        s.Configure(12, 11); Assert.Equal(1, s.Value); Assert.True(s.NotchChanged);
        s.Configure(12, 11); Assert.False(s.NotchChanged);
        s.SetValue(-4); Assert.Equal(0, s.Value);
    }

    [Fact]
    public void RepeatUsesInitialDelayThenOneDeadlineStepPerFrame()
    {
        var s = Create(); var p = new EnbUiPoint(108, 228);
        Assert.True(s.Begin(EnbScrollbarPart.Down, p, 1000)); Assert.Equal(1, s.Value);
        Assert.False(s.Advance(p, 1499)); Assert.Equal(1, s.Value);
        Assert.True(s.Advance(p, 1500)); Assert.Equal(2, s.Value); Assert.Equal(1600, s.RepeatAt);
        Assert.False(s.Advance(p, 1599));
        Assert.True(s.Advance(p, 1900)); Assert.Equal(3, s.Value); Assert.Equal(1700, s.RepeatAt);
        Assert.True(s.Advance(p, 1900)); Assert.Equal(4, s.Value); Assert.Equal(1800, s.RepeatAt);
        s.Cancel(); Assert.False(s.Advance(p, 9000)); Assert.Equal(4, s.Value);
    }

    [Fact]
    public void RepeatSuspendsOutsidePressedRegionWithoutResettingDeadline()
    {
        var s = Create(); var down = new EnbUiPoint(108, 228);
        s.Begin(EnbScrollbarPart.Down, down, 0);
        Assert.False(s.Advance(new(50, 228), 500)); Assert.Equal(500, s.RepeatAt);
        Assert.False(s.Advance(new(108, 8), 600)); Assert.Equal(1, s.Value);
        Assert.True(s.Advance(down, 700)); Assert.Equal(2, s.Value); Assert.Equal(600, s.RepeatAt);
    }

    [Fact]
    public void PageRepeatStopsWhenThumbReachesThePointer()
    {
        var s = Create(); var point = new EnbUiPoint(108, 130);
        Assert.Equal(EnbScrollbarPart.PageDown, s.Hit(point));
        s.Begin(EnbScrollbarPart.PageDown, point, 0); Assert.Equal(11, s.Value);
        Assert.True(s.Advance(point, 500)); Assert.Equal(22, s.Value);
        Assert.Equal(EnbScrollbarPart.Thumb, s.Hit(point));
        Assert.False(s.Advance(point, 600)); Assert.Equal(22, s.Value);
    }

    [Fact]
    public void DragPreservesGrabOffsetAndRestoresStartOutsideMargin()
    {
        var s = Create(); s.SetValue(7.25);
        double start = s.ThumbStart + 5;
        Assert.False(s.Begin(EnbScrollbarPart.Thumb, new(108, start), 0));
        Assert.True(s.Move(new(108, start + 20))); Assert.True(s.Value > 7.25);
        s.Move(new(67.9, start)); Assert.Equal(7.25, s.Value);
        s.Move(new(68, start + 20)); Assert.True(s.Value > 7.25);
        s.Move(new(108, 1000)); Assert.Equal(7.25, s.Value);
        s.Cancel(); Assert.False(s.Move(new(108, start + 20))); Assert.Equal(7.25, s.Value);
    }

    [Fact]
    public void HiddenSliderRetiresHeldInputAndAllFiveRegionsHaveNativeEdges()
    {
        var s = Create(); s.SetValue(10);
        Assert.Equal(EnbScrollbarPart.None, s.Hit(new(99, 10)));
        Assert.Equal(EnbScrollbarPart.Up, s.Hit(new(100, 4)));
        Assert.Equal(EnbScrollbarPart.PageUp, s.Hit(new(108, 20)));
        Assert.Equal(EnbScrollbarPart.Thumb, s.Hit(new(108, s.ThumbStart)));
        Assert.Equal(EnbScrollbarPart.PageDown, s.Hit(new(108, s.ThumbStart + s.ThumbLength)));
        Assert.Equal(EnbScrollbarPart.Down, s.Hit(new(116, 236)));
        s.Begin(EnbScrollbarPart.Down, new(108, 228), 0); s.Configure(5, 11);
        Assert.Equal(0, s.Value); Assert.Equal(EnbScrollbarPart.None, s.Pressed);
        Assert.Equal(EnbScrollbarPart.None, s.Hit(new(108, 228)));
        Assert.False(s.Begin(EnbScrollbarPart.Down, new(108, 228), 0));
    }

    [Fact]
    public void RepeatedInputAndConfigurationAllocateNothing()
    {
        var s = Create(); var p = new EnbUiPoint(108, 228);
        for (int i = 0; i < 1024; i++) { s.Configure(41, 11); s.Begin(EnbScrollbarPart.Down, p, 0); s.Advance(p, 500); s.Cancel(); s.SetValue(0); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++) { s.Configure(41, 11); s.Begin(EnbScrollbarPart.Down, p, 0); s.Advance(p, 500); s.Cancel(); s.SetValue(0); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
