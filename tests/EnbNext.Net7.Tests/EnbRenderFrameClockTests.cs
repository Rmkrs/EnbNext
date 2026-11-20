using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRenderFrameClockTests
{
    [Fact]
    public void ImmutableClientTimeAnchorAdvancesAtRenderCadence()
    {
        Assert.Equal(10_000u, EnbRenderFrameClock.AdvanceClientTime(10_000u, 1_000, 1_000, 10_000));
        Assert.Equal(10_016u, EnbRenderFrameClock.AdvanceClientTime(10_000u, 1_000, 1_160, 10_000));
        Assert.Equal(11_250u, EnbRenderFrameClock.AdvanceClientTime(10_000u, 1_000, 13_500, 10_000));
    }

    [Fact]
    public void ClientTimeAdvanceKeepsRetailUnsignedWraparound()
    {
        uint anchor = uint.MaxValue - 4u;
        Assert.Equal(5u, EnbRenderFrameClock.AdvanceClientTime(anchor, 100, 110, 1_000));
    }

    [Fact]
    public void InvalidFrequencyFailsClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EnbRenderFrameClock.AdvanceClientTime(0, 0, 1, 0));
    }
}
