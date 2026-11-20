using EnbNext.Presentation;
using EnbNext.Client;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbUiInputQueueTests
{
    [Fact]
    public void ModifierKeysAreSkippedButPressAndReleaseOccupySeparateFrames()
    {
        var queue = new EnbUiInputQueue();
        queue.SetPosition(.9f, .8f);
        queue.Push(new(0x10, 0, .1f, .2f)); queue.Push(new(1, 0x100, .3f, .4f));
        queue.Push(new(0x11, 0, .5f, .6f)); queue.Push(new(1, 0x1000, .7f, .8f));
        Assert.False(queue.LeftDown); // physical state can lead queued events
        Assert.Equal(new EnbUiInputEvent(1, 0x100, .3f, .4f), queue.TakeFrame());
        Assert.Equal(new EnbUiInputEvent(1, 0x1100, .7f, .8f), queue.TakeFrame());
        Assert.Equal(new EnbUiInputEvent(0, 0, .9f, .8f), queue.TakeFrame());
    }

    [Fact]
    public void FullRingDropsNewEventAfterUpdatingHeldState()
    {
        var queue = new EnbUiInputQueue();
        for (int i = 0; i < 254; i++) Assert.True(queue.Push(new(0x41, 0, i, 0)));
        Assert.False(queue.Push(new(1, 0x200, 1, 1)));
        Assert.True(queue.LeftDown); Assert.Equal(254, queue.Count);
        Assert.False(queue.Push(new(1, 0x1000, 1, 1)));
        Assert.False(queue.LeftDown);
        for (int i = 0; i < 254; i++) Assert.Equal(i, queue.TakeFrame().X);
        Assert.Equal(0, queue.Count);
        Assert.True(queue.Push(new(2, 0x400, 0, 0)));
        queue.Clear(); Assert.False(queue.RightDown); Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void RingWrapAndInitialPressModifiersSurviveRepeatAndRelease()
    {
        var queue = new EnbUiInputQueue();
        for (int i = 0; i < 1024; i++)
        {
            queue.Push(new(0x51, 0x400, .2f, .3f));
            queue.Push(new(0x51, 0x100, .2f, .3f));
            queue.Push(new(0x51, 0x1000, .2f, .3f));
            Assert.Equal(0x400, queue.TakeFrame().Modifiers);
            Assert.Equal(0x400, queue.TakeFrame().Modifiers);
            Assert.Equal(0x1400, queue.TakeFrame().Modifiers);
        }
    }

    [Theory]
    [InlineData(0x1b, 0, "quit/EXIT")]
    [InlineData(0x1b, 0x100, null)]
    [InlineData(0x1b, 0x400, null)]
    [InlineData(0x51, 0x400, "quit/BTTN00")]
    [InlineData(0x43, 0x400, "quit/BTTN01")]
    [InlineData(0x4c, 0x400, "quit/BTTN02")]
    [InlineData(0x51, 0x600, null)]
    [InlineData(0x51, 0x1400, null)]
    public void QuitBindingsCompareCompleteInputWord(ushort code, ushort modifiers, string? expected)
        => Assert.Equal(expected, EnbQuitMenuState.InputAction(code, modifiers));

    [Fact]
    public void ModelFontHoverHasStrictEdgesAndIntegerDisplayHalves()
    {
        var clip = new EnbUiRect(300, 220, 40, 40);
        Assert.True(EnbAttachedFontColor.Contains(clip, 640, 480, .5f, .5f));
        Assert.False(EnbAttachedFontColor.Contains(clip, 640, 480, 300f / 640, .5f));
        Assert.False(EnbAttachedFontColor.Contains(clip, 640, 480, 340f / 640, .5f));
        Assert.False(EnbAttachedFontColor.Contains(clip, 640, 480, float.NaN, .5f));
        // Odd physical extent has a half-pixel centered-cursor displacement.
        var narrow = new EnbUiRect(320, 239, .25, 2);
        Assert.False(EnbAttachedFontColor.Contains(narrow, 640, 480, .5f, .5f));
        Assert.False(EnbAttachedFontColor.Contains(narrow, 641, 481, .5f, .5f));
        Assert.True(EnbAttachedFontColor.Contains(narrow, 641, 481, 320.125f / 641, .5f));
    }

    [Fact]
    public void WarmQueueFramesAllocateNothing()
    {
        var queue = new EnbUiInputQueue();
        for (int i = 0; i < 1024; i++) { queue.Push(new(0x10, 0, .5f, .5f)); queue.TakeFrame(); }
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++) { queue.Push(new(0x10, 0, .5f, .5f)); queue.TakeFrame(); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }
}
