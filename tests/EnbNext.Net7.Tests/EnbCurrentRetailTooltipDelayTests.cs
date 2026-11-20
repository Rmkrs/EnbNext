using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbCurrentRetailTooltipDelayTests
{
    [Fact]
    public void PressingTheHoveredGadgetHidesItsTooltipAndRestartsTheDelay()
    {
        var delay = new EnbCurrentRetailTooltipDelay();
        Assert.False(delay.IsReady(3, 0, 600));
        Assert.True(delay.IsReady(3, 600, 600));
        // 006AB5B0: while the hovered gadget is the pressed one the tooltip is hidden
        // and its show time is pushed forward on every update.
        Assert.False(delay.IsReady(3, 700, 600, captured: true));
        Assert.False(delay.IsReady(3, 1500, 600, captured: true));
        Assert.False(delay.IsReady(3, 2000, 600));
        Assert.True(delay.IsReady(3, 2100, 600));
    }
}
