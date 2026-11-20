using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbQuitPointerTests
{
    // Native words from 0065F8xx: resolved edge plus current held/up state.
    [Theory]
    [InlineData(0, 0, false, false, 0x88)]
    [InlineData(1, 0, true, false, 0x83)]
    [InlineData(1, 0x1000, false, false, 0x8c)]
    [InlineData(2, 0, false, true, 0x38)]
    [InlineData(2, 0x1000, false, false, 0xc8)]
    [InlineData(1, 0x100, true, false, 0x283)]
    [InlineData(1, 0x200, true, false, 0x483)]
    [InlineData(1, 0x400, true, false, 0x883)]
    [InlineData(0x101, 0, false, false, 0x1000)]
    [InlineData(0x41, 0x400, false, false, 0x900)]
    public void NativeFrameFlagWords(int code, int modifiers, bool left, bool right, uint expected)
        => Assert.Equal(expected, new EnbUiPointerFrame((ushort)code, (ushort)modifiers, left, right).GadgetFlags);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void UnmodifiedPressActivatesAndReleaseOutsideOnlyReleasesCapture(ushort button)
    {
        var owner = new EnbUiGadgetPointer(5);
        Assert.Equal(2, owner.Dispatch(new(button, 0, button == 1, button == 2), 4));
        Assert.True(owner.Selected(2));
        Assert.Equal(2, button == 1 ? owner.LeftCapture : owner.RightCapture);
        Assert.Equal(-1, owner.Dispatch(new(0, 0, button == 1, button == 2), 0));
        Assert.True(owner.Consumed);
        Assert.Equal(-1, owner.Dispatch(new(button, 0x1000, false, false), 0));
        Assert.False(owner.Selected(2));
        Assert.Equal(-1, owner.LeftCapture); Assert.Equal(-1, owner.RightCapture);
        Assert.Equal(2, owner.LastHandler);
    }

    [Theory]
    [InlineData(0x100)]
    [InlineData(0x200)]
    [InlineData(0x400)]
    [InlineData(0x700)]
    public void ModifiedPressSelectsButDoesNotMatchCommand(ushort modifiers)
    {
        var owner = new EnbUiGadgetPointer(5);
        Assert.Equal(-1, owner.Dispatch(new(1, modifiers, true, false), 1));
        Assert.True(owner.Selected(0)); Assert.True(owner.Consumed);
        Assert.Equal(0, owner.LastHandler);
        Assert.Equal(-1, owner.Dispatch(new(1, (ushort)(modifiers | 0x1000), false, false), 1));
        Assert.False(owner.Selected(0));
    }

    [Fact]
    public void OverlapUsesReverseRegistrationAndBackgroundDoesNotActivate()
    {
        var owner = new EnbUiGadgetPointer(5);
        Assert.Equal(0, owner.Dispatch(new(1, 0, true, false), 63));
        Assert.True(owner.FirstEnabled(0)); Assert.False(owner.FirstEnabled(1));
        Assert.True(owner.Hit(5));
        owner.Retire();
        Assert.Equal(-1, owner.Dispatch(new(2, 0, false, true), 32));
        Assert.True(owner.Consumed); Assert.Equal(5, owner.LastHandler);
        Assert.Equal(-1, owner.RightCapture); Assert.False(owner.Flash(5));
        Assert.Equal(-1, owner.Dispatch(new(1, 0, true, false), 0));
        Assert.False(owner.Consumed);
    }

    [Fact]
    public void CapturedOtherButtonPressRewritesTheSameEvent()
    {
        var owner = new EnbUiGadgetPointer(5);
        Assert.Equal(1, owner.Dispatch(new(1, 0, true, false), 2));
        // Right press is routed to the left capture even over another button.
        Assert.Equal(1, owner.Dispatch(new(2, 0, true, true), 16));
        Assert.Equal(1, owner.LeftCapture); Assert.Equal(1, owner.RightCapture);
        Assert.Equal(-1, owner.Dispatch(new(2, 0x1000, true, false), 0));
        Assert.Equal(1, owner.LeftCapture); Assert.Equal(-1, owner.RightCapture);
        Assert.False(owner.Selected(1));
        owner.Retire();
        Assert.Equal(-1, owner.LeftCapture); Assert.Equal(-1, owner.LastHandler);
    }

    [Fact]
    public void FlashAdmissionAndRetirementUsePreviousFrameHitFlags()
    {
        var owner = new EnbUiGadgetPointer(5);
        owner.Dispatch(default, 1);
        Assert.False(owner.Flash(0));
        owner.Dispatch(default, 1);
        Assert.True(owner.Flash(0));
        owner.Dispatch(default, 0);
        Assert.True(owner.Flash(0));
        owner.Dispatch(default, 0);
        Assert.False(owner.Flash(0));
        // Direct press creates a selected clone; pressing an already flashed
        // gadget instead destroys that clone (0065FC11..0065FCB4).
        owner.Retire(); owner.Dispatch(new(1, 0, true, false), 1);
        Assert.True(owner.Selected(0)); Assert.True(owner.Flash(0));
        owner.Retire(); owner.Dispatch(default, 1); owner.Dispatch(default, 1);
        owner.Dispatch(new(1, 0, true, false), 1);
        Assert.True(owner.Selected(0)); Assert.False(owner.Flash(0));
    }

    [Fact]
    public void TooltipUsesRetainedHitDuringCaptureAndRetiresAfterReleaseTraversal()
    {
        var owner = new EnbUiGadgetPointer(5);
        owner.Dispatch(new(1, 0x100, true, false), 4);
        Assert.Equal(2, owner.TooltipGadget);
        owner.Dispatch(new(0, 0, true, false), 1);
        Assert.Equal(2, owner.TooltipGadget);
        owner.Dispatch(new(1, 0x1100, false, false), 0);
        Assert.Equal(2, owner.TooltipGadget);
        owner.Dispatch(default, 0);
        Assert.Equal(-1, owner.TooltipGadget);
    }

    [Fact]
    public void WarmFrameDispatchDoesNotAllocate()
    {
        var owner = new EnbUiGadgetPointer(5);
        for (int i = 0; i < 1024; i++) owner.Dispatch(default, 1);
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++) owner.Dispatch(default, 1);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }
}
