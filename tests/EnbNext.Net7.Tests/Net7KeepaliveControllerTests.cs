using EnbNext.Net7.Reliability;
using EnbNext.Net7.Timing;

namespace EnbNext.Net7.Tests;

public sealed class Net7KeepaliveControllerTests
{
    [Fact]
    public async Task StrictThirtySecondThreshold_SendsSectorThenMasterAndAdvancesBaseline()
    {
        var clock = new ManualClock(30000);
        var order = new List<string>();
        var keepalive = Create(clock, order, eligible: () => true);

        Assert.False(await keepalive.EvaluateOnceAsync());
        Assert.Empty(order);

        clock.Now = 30001;
        Assert.True(await keepalive.EvaluateOnceAsync());
        Assert.Equal(new[] { "sector", "master" }, order);
        Assert.Equal(30001u, keepalive.Baseline);
    }

    [Fact]
    public async Task SendFailuresDoNotPreventSecondAttemptOrBaselineAdvance()
    {
        var clock = new ManualClock(40000);
        var order = new List<string>();
        var keepalive = new Net7KeepaliveController(
            clock,
            _ => { order.Add("sector"); throw new IOException("sector"); },
            _ => { order.Add("master"); throw new InvalidOperationException("master"); },
            () => true);

        Assert.True(await keepalive.EvaluateOnceAsync());
        Assert.Equal(new[] { "sector", "master" }, order);
        Assert.Equal(40000u, keepalive.Baseline);
    }

    [Fact]
    public async Task EligibilityIsSampledOnceBeforeSectorThenMasterAttempt()
    {
        var clock = new ManualClock(40000);
        bool eligible = true;
        var order = new List<string>();
        var keepalive = new Net7KeepaliveController(
            clock,
            _ =>
            {
                order.Add("sector");
                eligible = false;
                return Task.FromResult(true);
            },
            _ =>
            {
                order.Add("master");
                return Task.FromResult(true);
            },
            () => eligible);

        Assert.True(await keepalive.EvaluateOnceAsync());
        Assert.False(eligible);
        Assert.Equal(new[] { "sector", "master" }, order);
        Assert.Equal(40000u, keepalive.Baseline);
    }

    [Fact]
    public async Task IneligibleIterationDoesNotAdvanceBaseline()
    {
        var clock = new ManualClock(50000);
        bool eligible = false;
        var order = new List<string>();
        var keepalive = Create(clock, order, () => eligible);

        Assert.False(await keepalive.EvaluateOnceAsync());
        Assert.Equal(0u, keepalive.Baseline);
        eligible = true;
        Assert.True(await keepalive.EvaluateOnceAsync());
        Assert.Equal(50000u, keepalive.Baseline);
    }

    [Fact]
    public async Task BackwardsObservationResetsBaselineWithoutSending()
    {
        var clock = new ManualClock(40000);
        var order = new List<string>();
        var keepalive = Create(clock, order, () => true);
        Assert.True(await keepalive.EvaluateOnceAsync());

        order.Clear();
        clock.Now = 10;
        Assert.False(await keepalive.EvaluateOnceAsync());
        Assert.Empty(order);
        Assert.Equal(10u, keepalive.Baseline);
    }

    [Fact]
    public void InboundKeepaliveDoesNotMutatePeriodicBaselineOrMultiplier()
    {
        var clock = new ManualClock(0);
        var keepalive = Create(clock, [], () => true);
        keepalive.ObserveServerFrequency(17);
        keepalive.ObserveInboundKeepalive();
        Assert.Equal(0u, keepalive.Baseline);
        Assert.Equal(17, keepalive.CurrentLoopMultiplier);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(0, 2)]
    [InlineData(21, 2)]
    public void ServerMultiplier_IsAcceptedOnlyInOneThroughTwenty(uint value, int expected)
    {
        var clock = new ManualClock(100);
        var keepalive = Create(clock, [], () => true);
        keepalive.ObserveServerFrequency(value);
        Assert.Equal(expected, keepalive.CurrentLoopMultiplier);
    }

    [Fact]
    public void TwoSecondActivityWindow_OverwritesPriorServerChoiceWithTwoOnExpiry()
    {
        var clock = new ManualClock(100);
        var keepalive = Create(clock, [], () => true);
        keepalive.ObserveServerFrequency(17);
        Assert.Equal(17, keepalive.CurrentLoopMultiplier);

        keepalive.MarkActiveForTwoSeconds();
        clock.Now = 2000;
        Assert.Equal(1, keepalive.CurrentLoopMultiplier);
        clock.Now = 2100;
        Assert.Equal(1, keepalive.CurrentLoopMultiplier);
        clock.Now = 2101;
        Assert.Equal(2, keepalive.CurrentLoopMultiplier);
        Assert.Equal(2, keepalive.CurrentLoopMultiplier);

        keepalive.ObserveServerFrequency(9);
        Assert.Equal(9, keepalive.CurrentLoopMultiplier);
    }

    [Fact]
    public void TwoSecondActivityWindow_IsWrapSafeAcrossFullU32Clock()
    {
        var clock = new ManualClock(0xFFFFFF00u);
        var keepalive = Create(clock, [], () => true);
        keepalive.ObserveServerFrequency(17);

        keepalive.MarkActiveForTwoSeconds();
        clock.Now = unchecked(0xFFFFFF00u + 2000u);
        Assert.Equal(1, keepalive.CurrentLoopMultiplier);

        clock.Now = unchecked(0xFFFFFF00u + 2001u);
        Assert.Equal(2, keepalive.CurrentLoopMultiplier);
    }

    [Fact]
    public void ConstructorUsesCurrentImageDefaultTwentyAndNormalizesInvalidOverride()
    {
        var clock = new ManualClock(0);
        var normal = Create(clock, [], () => true);
        Assert.Equal(20, normal.CurrentLoopMultiplier);

        var invalid = new Net7KeepaliveController(
            clock,
            _ => Task.FromResult(true),
            _ => Task.FromResult(true),
            () => true,
            initialLoopMultiplier: 99);
        Assert.Equal(2, invalid.CurrentLoopMultiplier);
    }

    private static Net7KeepaliveController Create(
        ManualClock clock,
        List<string> order,
        Func<bool> eligible)
        => new(
            clock,
            _ => { order.Add("sector"); return Task.FromResult(true); },
            _ => { order.Add("master"); return Task.FromResult(true); },
            eligible);

    private sealed class ManualClock(uint now) : INet7RuntimeClock
    {
        public uint Now { get; set; } = now;
        public uint GetMilliseconds() => Now;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
