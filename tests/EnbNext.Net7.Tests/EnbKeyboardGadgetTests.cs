using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbKeyboardGadgetTests
{
    [Fact]
    public void CaptureConsumesSharedKeysAndCompletesOnlyOnRelease()
    {
        var owner = new EnbKeyboardGadget(); owner.Begin(); Assert.Equal("????", owner.Preview);
        var frame = new EnbUiPointerFrame(0x41, 0x300, false, false);
        Assert.True(owner.Handle(ref frame, frame.GadgetFlags, 0));
        Assert.True(owner.Enabled); Assert.Equal("Ctrl-Shift-A", owner.Preview);
        Assert.Equal((ushort)0, frame.Code); Assert.Equal((ushort)0, frame.Modifiers);
        frame = new(0x42, 0x400, false, false); owner.Handle(ref frame, frame.GadgetFlags, 0);
        Assert.Equal("Alt-B", owner.Preview);
        frame = new(0x41, 0x1300, false, false); owner.Handle(ref frame, frame.GadgetFlags, 0);
        Assert.False(owner.Enabled); Assert.Equal((ushort)0x41, owner.Code);
        Assert.Equal((ushort)0x300, owner.Modifiers); Assert.Equal("Ctrl-Shift-A", owner.Preview);
        owner.Begin(); Assert.True(owner.Enabled); Assert.Equal("????", owner.Preview);
    }

    [Fact]
    public void ModifierPollingRequiresAnObservedNonzeroToZeroTransition()
    {
        var owner = new EnbKeyboardGadget(); owner.Begin();
        var frame = new EnbUiPointerFrame(0, 0, false, false);
        owner.Handle(ref frame, frame.GadgetFlags, 0); Assert.True(owner.Enabled);
        owner.Handle(ref frame, frame.GadgetFlags, 0x600); Assert.Equal("Ctrl-Alt- ", owner.Preview);
        owner.Handle(ref frame, frame.GadgetFlags, 0x200); Assert.True(owner.Enabled);
        owner.Handle(ref frame, frame.GadgetFlags, 0); Assert.False(owner.Enabled); Assert.Equal((ushort)0, owner.Code);
        owner.Cancel(); Assert.Equal("????", owner.Preview);
    }

    [Fact]
    public void QueueTracksHeldModifiersEvenWhenEventsAreFilteredOrOverflow()
    {
        var queue = new EnbUiInputQueue();
        queue.Push(new(0x10, 0x100, 0, 0)); queue.Push(new(0x11, 0x300, 0, 0));
        Assert.Equal((ushort)0x300, queue.HeldModifiers); Assert.Equal((ushort)0, queue.TakeFrame().Code);
        for (int i = 0; i < 254; i++) queue.Push(new(0x41, 0, 0, 0));
        Assert.False(queue.Push(new(0x10, 0x1000, 0, 0)));
        Assert.Equal((ushort)0x200, queue.HeldModifiers); queue.Clear(); Assert.Equal((ushort)0, queue.HeldModifiers);
    }

    [Fact]
    public void WarmUnchangedPreviewAndMouseFramesAllocateNothing()
    {
        var owner = new EnbKeyboardGadget(); owner.Begin();
        var frame = new EnbUiPointerFrame(1, 0, true, false);
        owner.Handle(ref frame, frame.GadgetFlags, 0x200);
        var preview = owner.Preview;
        for (int i = 0; i < 100; i++) owner.Handle(ref frame, frame.GadgetFlags, 0x200);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 4096; i++) owner.Handle(ref frame, frame.GadgetFlags, 0x200);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before); Assert.Same(preview, owner.Preview);
        Assert.Equal((ushort)1, frame.Code); Assert.True(owner.Enabled);
    }

    [Fact]
    public void NumericCaptureRecognizesAliasesAndPreservesUnknownCodes()
    {
        var ini = EnbRetailIniDocument.Parse("[First]\nText=First action\nContext=Targeting\nPSW_1=D\n[Second]\nText=Second action\nContext=Targeting\nPSW_1=Del\n");
        var draft = new EnbRetailControlOptions(ini, EnbRetailKeyBindings.Resolve(ini, EnbRetailIniDocument.Parse(""), null));
        var dialog = new EnbConfirmationState(); var capture = new EnbControlCaptureState(dialog);
        capture.Begin("First", 1); capture.Complete(draft, 0x2e, 0);
        Assert.Contains("Ext Del is already tied to", dialog.Text); Assert.Equal("D", draft.Binding("First", 1)!.Key);
        capture.Answer(draft, true); Assert.Null(draft.Binding("Second", 1)); Assert.Equal((ushort)0x2e, draft.Binding("First", 1)!.Code);
        capture.Begin("First", 2); capture.Complete(draft, 0x80, 0x200);
        var unknown = draft.Binding("First", 2)!; Assert.Equal((ushort)0x80, unknown.Code);
        Assert.Equal("", unknown.Key); Assert.Equal("Ctrl-", unknown.Display); Assert.Null(dialog.Text);
        capture.Begin("First", 2); capture.Complete(draft, 0x2c, 0x200);
        Assert.Equal("Ctrl-Prnt Scrn is not allowed as a hot key.", dialog.Text);
    }
}
