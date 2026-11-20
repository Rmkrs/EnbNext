using System.Buffers.Binary;
using EnbNext.Net7.Reliability;
using EnbNext.Net7.Timing;

namespace EnbNext.Net7.Tests;

public sealed class Net7LoginResetControllerTests
{
    [Fact]
    public void Rel11ExactCadenceIsNotPublicRuntimeConfiguration()
    {
        const System.Reflection.BindingFlags publicInstance =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;

        Assert.Null(typeof(Net7ServerOptions).GetProperty("MasterRequestAttempts", publicInstance));
        Assert.Null(typeof(Net7ServerOptions).GetProperty("MasterResponsePollsPerAttempt", publicInstance));
        Assert.Null(typeof(Net7ServerOptions).GetProperty("MasterResponsePollInterval", publicInstance));

        Assert.DoesNotContain(
            typeof(Net7LoginResetController).GetConstructors(publicInstance),
            constructor => constructor.GetParameters().Any(parameter =>
                parameter.Name is "attempts" or "pollsPerAttempt" or "pollInterval"));

        Assert.Equal(20, Net7ServerOptions.CurrentGenericRequestAttempts);
        Assert.Equal(4, Net7ServerOptions.CurrentGenericResponsePollsPerAttempt);
        Assert.Equal(TimeSpan.FromMilliseconds(250), Net7ServerOptions.CurrentGenericResponsePollInterval);
    }

    [Fact]
    public async Task NoAckUsesExactTwentyByFourBudgetAndFixedIdPayload()
    {
        var clock = new CountingClock();
        var sent = new List<byte[]>();
        var controller = new Net7LoginResetController(
            clock,
            (payload, _) =>
            {
                sent.Add(payload.ToArray());
                return Task.FromResult(true);
            });

        bool acknowledged = await controller.RunAsync(0xAABBCCDD);

        Assert.False(acknowledged);
        Assert.Equal(20, sent.Count);
        Assert.Equal(80, clock.DelayCount);
        Assert.All(sent, payload => Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32LittleEndian(payload)));
        Assert.False(controller.TransactionActive);
    }

    [Fact]
    public async Task Routed3007CanShortenOnlyTheCurrentlyActiveTransaction()
    {
        var clock = new CountingClock();
        Net7LoginResetController? controller = null;
        int sends = 0;
        controller = new Net7LoginResetController(
            clock,
            (_, _) =>
            {
                sends++;
                controller!.ObserveDirectOuter3007();
                return Task.FromResult(true);
            });

        controller.ObserveDirectOuter3007(); // stale/early ACK must not persist.
        bool acknowledged = await controller.RunAsync(0x01020304);

        Assert.True(acknowledged);
        Assert.Equal(1, sends);
        Assert.Equal(0, clock.DelayCount);
        Assert.False(controller.TransactionActive);
    }

    [Fact]
    public async Task LocalSendFailureConsumesAttemptBudgetRatherThanBecomingSuccess()
    {
        var clock = new CountingClock();
        int sends = 0;
        var controller = new Net7LoginResetController(
            clock,
            (_, _) =>
            {
                sends++;
                throw new IOException("offline fixture send failure");
            },
            attempts: 2,
            pollsPerAttempt: 2,
            pollInterval: TimeSpan.FromMilliseconds(10));

        bool acknowledged = await controller.RunAsync(7);

        Assert.False(acknowledged);
        Assert.Equal(2, sends);
        Assert.Equal(4, clock.DelayCount);
    }

    private sealed class CountingClock : INet7RuntimeClock
    {
        private uint _now;
        public int DelayCount { get; private set; }
        public uint GetMilliseconds() => _now;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DelayCount++;
            _now = unchecked(_now + checked((uint)Math.Max(0, (long)delay.TotalMilliseconds)));
            return Task.CompletedTask;
        }
    }
}
