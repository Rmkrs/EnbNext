using EnbNext.Engine;

namespace EnbNext.Net7.Tests;

public sealed class EnbTextEditorFocusTests
{
    [Fact]
    public void GadgetFocusFollowsRetailCaptureOwnership()
    {
        using var first = new EnbRetailTextEditor(64, true);
        using var second = new EnbRetailTextEditor(64, true);
        first.Pointer.CaptureContext.Add(second.Pointer);
        uint flags = 0;
        // 00673150: focus only when no gadget holds the capture.
        Assert.True(first.FocusGadget(false, ref flags));
        Assert.False(first.FocusGadget(false, ref flags));
        Assert.False(second.FocusGadget(false, ref flags));
        // 006732A0: a forced focus takes the capture from another gadget, never from itself.
        Assert.True(second.FocusGadget(true, ref flags));
        Assert.False(second.FocusGadget(true, ref flags));
    }
}
