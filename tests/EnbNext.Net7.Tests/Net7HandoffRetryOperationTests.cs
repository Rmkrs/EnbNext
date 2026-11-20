using EnbNext.Net7.Reliability;
using EnbNext.Net7.Timing;

namespace EnbNext.Net7.Tests;

public sealed class Net7HandoffRetryOperationTests
{
    [Theory]
    [InlineData(0x07, true)]
    [InlineData(0x08, true)]
    [InlineData(0x13, true)]
    [InlineData(0x12, false)]
    [InlineData(0x1C, false)]
    [InlineData(0x1D, false)]
    public void ActionRetryTriggerSetIsExact(int subtype, bool expected)
        => Assert.Equal(expected, Net7HandoffRetryOperation.ActionSubtypeUsesRetry(checked((byte)subtype)));

    [Fact]
    public async Task ActionEarlyHandoffBeforeInitialDelayProducesZeroRetries()
    {
        var clock = new CallbackClock();
        int sends = 0;
        var op = Net7HandoffRetryOperation.CreateAction(
            clock,
            (_, _, _) => { sends++; return Task.FromResult(true); },
            Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
        clock.AfterDelay = (_, count) => { if (count == 1) op.ObserveServerHandoff(); };

        int count = await op.RunAsync();

        Assert.Equal(0, count);
        Assert.Equal(0, sends);
        Assert.Equal(new[] { 1500 }, clock.Delays);
    }

    [Fact]
    public async Task ActionOwnerInvalidationAfterRetryOnePreventsRetryTwo()
    {
        var clock = new CallbackClock();
        bool ownerIsCurrent = true;
        int sends = 0;
        var op = Net7HandoffRetryOperation.CreateAction(
            clock,
            (_, _, _) => { sends++; return Task.FromResult(true); },
            new byte[16],
            () => ownerIsCurrent);
        // Delay #1 is initial. Delay #2 follows retry #1 and models a session
        // generation switch while the old worker is asleep.
        clock.AfterDelay = (_, count) => { if (count == 2) ownerIsCurrent = false; };

        int count = await op.RunAsync();

        Assert.Equal(1, count);
        Assert.Equal(1, sends);
        Assert.Equal(new[] { 1500, 1500 }, clock.Delays);
    }

    [Fact]
    public async Task ActionNoHandoffProducesExactlyFiveCopiesAndFinalPostSendDelay()
    {
        var clock = new CallbackClock();
        var sent = new List<(ushort Opcode, byte[] Payload)>();
        byte[] payload = Enumerable.Range(0, 16).Select(i => (byte)(0x80 + i)).ToArray();
        var op = Net7HandoffRetryOperation.CreateAction(
            clock,
            (opcode, bytes, _) =>
            {
                sent.Add((opcode, bytes.ToArray()));
                return Task.FromResult(true);
            },
            payload);

        int count = await op.RunAsync();

        Assert.Equal(5, count);
        Assert.Equal(5, sent.Count);
        Assert.All(sent, x => { Assert.Equal((ushort)0x002C, x.Opcode); Assert.Equal(payload, x.Payload); });
        Assert.Equal(6, clock.Delays.Count); // initial + after each of five sends
        Assert.All(clock.Delays, ms => Assert.Equal(1500, ms));
    }

    [Fact]
    public async Task ActionHandoffAfterRetryTwoPreventsRetryThree()
    {
        var clock = new CallbackClock();
        int sends = 0;
        Net7HandoffRetryOperation? op = null;
        op = Net7HandoffRetryOperation.CreateAction(
            clock,
            (_, _, _) => { sends++; return Task.FromResult(true); },
            new byte[16]);
        // Delay #1 is initial, #2 follows retry1, #3 follows retry2.
        clock.AfterDelay = (_, count) => { if (count == 3) op.ObserveServerHandoff(); };

        int count = await op.RunAsync();

        Assert.Equal(2, count);
        Assert.Equal(2, sends);
    }

    [Fact]
    public async Task LeaveNoHandoffProducesExactlyTenCopiesAndElevenOneSecondDelays()
    {
        var clock = new CallbackClock();
        var sent = new List<(ushort Opcode, byte[] Payload)>();
        byte[] payload = Enumerable.Range(0, 9).Select(i => (byte)(0x20 + i)).ToArray();
        var op = Net7HandoffRetryOperation.CreateLeaveStarbase(
            clock,
            (opcode, bytes, _) =>
            {
                sent.Add((opcode, bytes.ToArray()));
                return Task.FromResult(true);
            },
            payload);

        int count = await op.RunAsync();

        Assert.Equal(10, count);
        Assert.Equal(10, sent.Count);
        Assert.All(sent, x => { Assert.Equal((ushort)0x004E, x.Opcode); Assert.Equal(payload, x.Payload); });
        Assert.Equal(11, clock.Delays.Count);
        Assert.All(clock.Delays, ms => Assert.Equal(1000, ms));
    }


    [Fact]
    public async Task LeaveEarlyHandoffBeforeInitialDelayProducesZeroRetries()
    {
        var clock = new CallbackClock();
        int sends = 0;
        Net7HandoffRetryOperation? op = null;
        op = Net7HandoffRetryOperation.CreateLeaveStarbase(
            clock,
            (_, _, _) => { sends++; return Task.FromResult(true); },
            new byte[9]);
        clock.AfterDelay = (_, count) => { if (count == 1) op.ObserveServerHandoff(); };

        int count = await op.RunAsync();

        Assert.Equal(0, count);
        Assert.Equal(0, sends);
        Assert.Equal(new[] { 1000 }, clock.Delays);
    }

    [Fact]
    public async Task LeaveHandoffAfterRetryOnePreventsRetryTwo()
    {
        var clock = new CallbackClock();
        int sends = 0;
        Net7HandoffRetryOperation? op = null;
        op = Net7HandoffRetryOperation.CreateLeaveStarbase(
            clock,
            (_, _, _) => { sends++; return Task.FromResult(true); },
            new byte[9]);
        clock.AfterDelay = (_, count) => { if (count == 2) op.ObserveServerHandoff(); };

        int count = await op.RunAsync();

        Assert.Equal(1, count);
        Assert.Equal(1, sends);
        Assert.Equal(new[] { 1000, 1000 }, clock.Delays);
    }

    [Fact]
    public async Task LeaveHandoffAfterRetryFiveProducesExactlyFiveRetries()
    {
        var clock = new CallbackClock();
        int sends = 0;
        Net7HandoffRetryOperation? op = null;
        op = Net7HandoffRetryOperation.CreateLeaveStarbase(
            clock,
            (_, _, _) => { sends++; return Task.FromResult(true); },
            new byte[9]);
        // Initial delay is #1; the delay following retry #5 is #6.
        clock.AfterDelay = (_, count) => { if (count == 6) op.ObserveServerHandoff(); };

        int count = await op.RunAsync();

        Assert.Equal(5, count);
        Assert.Equal(5, sends);
        Assert.Equal(6, clock.Delays.Count);
    }

    [Fact]
    public async Task LeaveOwnerInvalidationAfterRetryOnePreventsRetryTwo()
    {
        var clock = new CallbackClock();
        bool ownerIsCurrent = true;
        int sends = 0;
        var op = Net7HandoffRetryOperation.CreateLeaveStarbase(
            clock,
            (_, _, _) => { sends++; return Task.FromResult(true); },
            new byte[9],
            () => ownerIsCurrent);
        clock.AfterDelay = (_, count) => { if (count == 2) ownerIsCurrent = false; };

        int count = await op.RunAsync();

        Assert.Equal(1, count);
        Assert.Equal(1, sends);
        Assert.Equal(new[] { 1000, 1000 }, clock.Delays);
    }

    [Fact]
    public async Task LeaveOperationsOwnIndependentImmutablePayloadCopies()
    {
        var clockA = new CallbackClock();
        var clockB = new CallbackClock();
        byte[] sourceA = Enumerable.Range(0, 9).Select(i => (byte)(0x10 + i)).ToArray();
        byte[] sourceB = Enumerable.Range(0, 9).Select(i => (byte)(0x80 + i)).ToArray();
        byte[] expectedA = sourceA.ToArray();
        byte[] expectedB = sourceB.ToArray();
        var sentA = new List<byte[]>();
        var sentB = new List<byte[]>();

        var opA = Net7HandoffRetryOperation.CreateLeaveStarbase(
            clockA,
            (_, bytes, _) => { sentA.Add(bytes.ToArray()); return Task.FromResult(true); },
            sourceA);
        var opB = Net7HandoffRetryOperation.CreateLeaveStarbase(
            clockB,
            (_, bytes, _) => { sentB.Add(bytes.ToArray()); return Task.FromResult(true); },
            sourceB);
        Array.Fill(sourceA, (byte)0xEE);
        Array.Fill(sourceB, (byte)0xFF);

        await opA.RunAsync();
        await opB.RunAsync();

        Assert.Equal(10, sentA.Count);
        Assert.Equal(10, sentB.Count);
        Assert.All(sentA, payload => Assert.Equal(expectedA, payload));
        Assert.All(sentB, payload => Assert.Equal(expectedB, payload));
    }

    [Fact]
    public async Task CancellationDuringDelayCancelsStaleOperationBeforeAnotherSend()
    {
        using var cts = new CancellationTokenSource();
        var clock = new CancelOnSecondDelayClock(cts);
        int sends = 0;
        var op = Net7HandoffRetryOperation.CreateLeaveStarbase(
            clock,
            (_, _, _) => { sends++; return Task.FromResult(true); },
            new byte[9]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => _ = await op.RunAsync(cts.Token));
        Assert.Equal(1, sends);
    }

    private sealed class CallbackClock : INet7RuntimeClock
    {
        public List<int> Delays { get; } = [];
        public Action<int, int>? AfterDelay { get; set; }
        public uint GetMilliseconds() => 0;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int ms = checked((int)delay.TotalMilliseconds);
            Delays.Add(ms);
            AfterDelay?.Invoke(ms, Delays.Count);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class CancelOnSecondDelayClock(CancellationTokenSource cts) : INet7RuntimeClock
    {
        private int _count;
        public uint GetMilliseconds() => 0;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            _count++;
            if (_count == 2)
                cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
