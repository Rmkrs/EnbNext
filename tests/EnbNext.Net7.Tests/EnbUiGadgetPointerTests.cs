using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbUiGadgetPointerTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(1, 0x100)]
    [InlineData(2, 0x200)]
    [InlineData(1, 0x400)]
    [InlineData(2, 0x700)]
    public void CheckboxTogglesOnPressWithoutCaptureEvenWhenPanelCommandDoesNotMatch(ushort button, ushort modifiers)
    {
        // Camera reverse registration: seven checks, Cancel/OK/Exit, background.
        var owner = new EnbUiGadgetPointer(10, 0x7f);
        Assert.Equal(modifiers == 0 ? 6 : -1, owner.Dispatch(new(button, modifiers, button == 1, button == 2), 1u << 6));
        Assert.Equal(6, owner.ToggleHandler);
        Assert.True(owner.Consumed);
        Assert.False(owner.Selected(6));
        Assert.Equal(-1, owner.LeftCapture); Assert.Equal(-1, owner.RightCapture);
        Assert.Equal(-1, owner.Dispatch(new(0, 0, button == 1, button == 2), 1u << 6));
        Assert.Equal(-1, owner.ToggleHandler);
        Assert.Equal(-1, owner.Dispatch(new(button, (ushort)(modifiers | 0x1000), false, false), 1u << 6));
        Assert.Equal(-1, owner.ToggleHandler);
    }

    [Fact]
    public void CameraButtonCapturePrecedesCheckboxAndRetirementClearsState()
    {
        var owner = new EnbUiGadgetPointer(10, 0x7f);
        Assert.Equal(8, owner.Dispatch(new(1, 0, true, false), 1u << 8));
        Assert.Equal(8, owner.LeftCapture); Assert.True(owner.Selected(8));
        Assert.Equal(8, owner.Dispatch(new(2, 0, true, true), 1));
        Assert.Equal(-1, owner.ToggleHandler);
        Assert.Equal(8, owner.RightCapture);
        owner.Retire();
        Assert.Equal(-1, owner.LeftCapture); Assert.Equal(-1, owner.RightCapture);
        Assert.Equal(-1, owner.ToggleHandler); Assert.False(owner.Selected(8));
        Assert.Equal(0, owner.Dispatch(new(2, 0, false, true), 0x7ff));
        Assert.Equal(0, owner.ToggleHandler);
        Assert.True(owner.Hit(10)); Assert.False(owner.FirstEnabled(1));
    }

    [Fact]
    public void MainLastButtonAndBackgroundHaveNoQuitSpecificIndices()
    {
        var owner = new EnbUiGadgetPointer(8);
        Assert.Equal(7, owner.Dispatch(new(1, 0, true, false), 1u << 7));
        Assert.True(owner.Selected(7)); Assert.Equal(7, owner.LeftCapture);
        owner.Retire();
        Assert.Equal(-1, owner.Dispatch(new(2, 0, false, true), 1u << 8));
        Assert.True(owner.Consumed); Assert.Equal(8, owner.LastHandler);
        Assert.False(owner.Flash(8)); Assert.Equal(-1, owner.RightCapture);
    }

    [Fact]
    public void CheckboxSteadyDispatchDoesNotAllocate()
    {
        var owner = new EnbUiGadgetPointer(10, 0x7f);
        for (int i = 0; i < 1024; i++) { owner.Dispatch(new(1, 0, true, false), 1); owner.Dispatch(new(1, 0x1000, false, false), 1); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++) { owner.Dispatch(new(1, 0, true, false), 1); owner.Dispatch(new(1, 0x1000, false, false), 1); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(64)]
    [InlineData(127)]
    public void WideCollectionPreservesCaptureAndDoesNotAliasCheckboxBits(int button)
    {
        var owner = new EnbUiGadgetPointer(button + 1, uint.MaxValue);
        var hits = new bool[button + 2]; hits[button] = hits[^1] = true;
        Assert.Equal(button, owner.Dispatch(new(1, 0, true, false), hits));
        Assert.Equal(-1, owner.ToggleHandler); Assert.Equal(button, owner.LeftCapture);
        Assert.True(owner.Selected(button)); Assert.True(owner.Hit(button + 1));
        Array.Clear(hits); hits[0] = true;
        Assert.Equal(button, owner.Dispatch(new(2, 0, true, true), hits));
        Assert.Equal(button, owner.RightCapture); Assert.Equal(-1, owner.ToggleHandler);
        owner.Dispatch(new(1, 0x1000, false, true), hits);
        Assert.Equal(-1, owner.LeftCapture); Assert.Equal(button, owner.RightCapture);
        owner.Dispatch(new(2, 0x1000, false, false), hits);
        Assert.Equal(-1, owner.RightCapture); Assert.False(owner.Selected(button));
        owner.Retire();
        Assert.Equal(0, owner.Dispatch(new(1, 0, true, false), hits));
        Assert.Equal(0, owner.ToggleHandler); Assert.Equal(-1, owner.LeftCapture);
        owner.Retire(); Array.Clear(hits); hits[^1] = true;
        Assert.Equal(-1, owner.Dispatch(new(1, 0, true, false), hits));
        Assert.Equal(button + 1, owner.LastHandler); Assert.True(owner.Consumed);
        Assert.False(owner.Flash(button + 1));
    }

    [Fact]
    public void OrderedHitsMatchMaskForEveryObservableStateAcrossEvents()
    {
        var maskOwner = new EnbUiGadgetPointer(31, 0x4a51u);
        var spanOwner = new EnbUiGadgetPointer(31, 0x4a51u);
        var random = new Random(27831); var hits = new bool[32];
        ushort[] codes = [0, 1, 2, 0x10, 0x11, 0x12, 0x101, 0x102, 0x1b, 0x41];
        for (int step = 0; step < 4096; step++)
        {
            uint mask = (uint)random.NextInt64(0, 1L << 32);
            for (int i = 0; i < hits.Length; i++) hits[i] = (mask & (1u << i)) != 0;
            var frame = new EnbUiPointerFrame(codes[random.Next(codes.Length)],
                (ushort)((random.Next(8) << 8) | (random.Next(2) << 12)), random.Next(2) != 0, random.Next(2) != 0);
            Assert.Equal(maskOwner.Dispatch(frame, mask), spanOwner.Dispatch(frame, hits));
            Assert.Equal(maskOwner.LeftCapture, spanOwner.LeftCapture); Assert.Equal(maskOwner.RightCapture, spanOwner.RightCapture);
            Assert.Equal(maskOwner.LastHandler, spanOwner.LastHandler); Assert.Equal(maskOwner.ToggleHandler, spanOwner.ToggleHandler);
            Assert.Equal(maskOwner.TooltipGadget, spanOwner.TooltipGadget); Assert.Equal(maskOwner.Consumed, spanOwner.Consumed);
            for (int i = 0; i < hits.Length; i++)
            {
                Assert.Equal(maskOwner.Selected(i), spanOwner.Selected(i)); Assert.Equal(maskOwner.Hit(i), spanOwner.Hit(i));
                Assert.Equal(maskOwner.FirstEnabled(i), spanOwner.FirstEnabled(i)); Assert.Equal(maskOwner.Flash(i), spanOwner.Flash(i));
                Assert.Equal(maskOwner.FlashInside(i), spanOwner.FlashInside(i));
            }
            if (step % 47 == 0) { maskOwner.Retire(); spanOwner.Retire(); }
        }
    }

    [Fact]
    public void WideDispatchAllocatesNothingAndRejectsIncompleteHitSets()
    {
        var owner = new EnbUiGadgetPointer(65); var hits = new bool[66]; hits[64] = true;
        for (int i = 0; i < 1024; i++) { owner.Dispatch(new(1, 0, true, false), hits); owner.Dispatch(new(1, 0x1000, false, false), hits); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1024; i++) { owner.Dispatch(new(1, 0, true, false), hits); owner.Dispatch(new(1, 0x1000, false, false), hits); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Throws<InvalidOperationException>(() => owner.Dispatch(default, 1u));
        Assert.Throws<ArgumentException>(() => owner.Dispatch(default, new bool[65]));
        Assert.Throws<ArgumentException>(() => owner.Dispatch(default, new bool[67]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EnbUiGadgetPointer(-1));
    }
}
