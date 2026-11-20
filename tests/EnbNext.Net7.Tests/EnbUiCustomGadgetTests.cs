using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbUiCustomGadgetTests
{
    private sealed class Handler : IEnbUiGadgetHandler
    {
        public bool Keyboard, ClearEvent;
        public int Calls;
        public uint LastFlags;
        public ushort LastCode;
        public bool HasFlash(int index) => index > 1;
        public bool TryHandle(int index, ref EnbUiPointerFrame frame, uint flags, out EnbUiGadgetResponse response)
        {
            response = default;
            if (index > 1) return false;
            Calls++; LastFlags = flags; LastCode = frame.Code;
            if (index == 0)
            {
                if (!Keyboard) return true;
                if (ClearEvent) frame = frame with { Code = 0, Modifiers = 0 };
                response = new((flags & 0x100) != 0); return true;
            }
            var change = (flags & 1) != 0 ? EnbUiCaptureChange.TakeLeft
                : (flags & 4) != 0 ? EnbUiCaptureChange.ReleaseLeft
                : (flags & 0x10) != 0 ? EnbUiCaptureChange.TakeRight
                : (flags & 0x40) != 0 ? EnbUiCaptureChange.ReleaseRight : EnbUiCaptureChange.None;
            response = new((flags & 0x1077) != 0, (flags & 0x55) != 0, change,
                (flags & 0x11) != 0 ? true : (flags & 0x44) != 0 ? false : null);
            return true;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CustomCapturePrecedesButtonsAndReleasesOutside(bool right)
    {
        var owner = new Handler(); var pointer = new EnbUiGadgetPointer(3, custom: owner);
        bool[] hits = [false, true, true, true]; ushort code = right ? (ushort)2 : (ushort)1;
        Assert.Equal(1, pointer.Dispatch(new(code, 0, !right, right), hits));
        Assert.Equal(1, right ? pointer.RightCapture : pointer.LeftCapture); Assert.True(pointer.Selected(1)); Assert.False(pointer.Flash(1));
        hits[1] = false;
        pointer.Dispatch(new(0, 0, !right, right), hits);
        Assert.True(pointer.Consumed); Assert.Equal(1, right ? pointer.RightCapture : pointer.LeftCapture);
        pointer.Dispatch(new(code, 0x1000, false, false), hits);
        Assert.Equal(-1, pointer.LeftCapture); Assert.Equal(-1, pointer.RightCapture); Assert.False(pointer.Selected(1));
        Assert.False(pointer.Selected(2)); pointer.Retire(); Assert.False(pointer.Consumed);
    }

    [Fact]
    public void SharedEventClearingDoesNotRecomputeOriginalFlags()
    {
        var owner = new Handler { Keyboard = true, ClearEvent = true };
        var pointer = new EnbUiGadgetPointer(3, custom: owner);
        bool[] hits = [true, true, true, true];
        // Keyboard does not consume this mouse press, but clears the shared event.
        var frame = new EnbUiPointerFrame(1, 0, true, false);
        // The later slider rewrites the cleared event again; 0065F4B0 binds
        // that gadget, regardless of the original event code.
        Assert.Equal(1, pointer.Dispatch(ref frame, hits)); Assert.Equal((ushort)0, frame.Code);
        Assert.Equal((ushort)0, owner.LastCode); Assert.True((owner.LastFlags & 1) != 0);
        Assert.Equal(1, pointer.LeftCapture); Assert.True(pointer.Selected(1)); Assert.False(pointer.Flash(0));
        pointer.Retire();
        Assert.Equal(-1, pointer.Dispatch(new(0x41, 0, false, false), hits));
        Assert.True(pointer.Consumed); Assert.Equal(-1, pointer.LeftCapture); Assert.False(pointer.Selected(2));
    }

    [Fact]
    public void StandardButtonsKeepTheirFlashAndCommandPath()
    {
        var pointer = new EnbUiGadgetPointer(3, custom: new Handler());
        bool[] hits = [false, false, true, true];
        pointer.Dispatch(new(0, 0, false, false), hits); pointer.Dispatch(new(0, 0, false, false), hits);
        Assert.True(pointer.Flash(2));
        Assert.Equal(2, pointer.Dispatch(new(1, 0, true, false), hits)); Assert.Equal(2, pointer.LeftCapture);
        pointer.Retire(); Assert.False(pointer.Flash(2)); Assert.False(pointer.Selected(2));
    }

    private sealed class SourceHandler : IEnbUiGadgetHandler
    {
        public int Previous = 1, Current = 1;
        public bool HasFlash(int index) => true;
        public int FlashSource(int index, bool beforeHitQuery) => beforeHitQuery ? Previous : Current;
        public bool TryHandle(int index, ref EnbUiPointerFrame frame, uint flags, out EnbUiGadgetResponse response)
        { response = default; return false; }
    }

    [Fact]
    public void FlashRefreshUsesPreviousSourceUntilTheNextUncapturedQuery()
    {
        var handler = new SourceHandler(); var pointer = new EnbUiGadgetPointer(1, custom: handler);
        bool[] hits = [true, true];
        pointer.Dispatch(new(0, 0, false, false), hits);
        pointer.Dispatch(new(0, 0, false, false), hits); Assert.Equal(1, pointer.FlashSource(0));
        handler.Current = 3;
        pointer.Dispatch(new(0, 0, false, false), hits); Assert.Equal(1, pointer.FlashSource(0));
        handler.Previous = 3;
        pointer.Dispatch(new(0, 0, false, false), hits); Assert.Equal(3, pointer.FlashSource(0));
        handler.Previous = handler.Current = -1;
        pointer.Dispatch(new(0, 0, false, false), hits); Assert.False(pointer.Flash(0));
    }

    [Fact]
    public void WarmCustomDispatchDoesNotAllocate()
    {
        var pointer = new EnbUiGadgetPointer(3, custom: new Handler()); bool[] hits = [false, true, true, true];
        void Cycle() { pointer.Dispatch(new(1, 0, true, false), hits); pointer.Dispatch(new(0, 0, true, false), hits); pointer.Dispatch(new(1, 0x1000, false, false), hits); }
        for (int i = 0; i < 100; i++) Cycle();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4096; i++) Cycle();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
