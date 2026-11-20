using System.Buffers.Binary;
using EnbNext.Engine;

namespace EnbNext.Net7.Tests;

/// <summary>Next connection indicator: latency comes only from the answer to the client's own latest time request.</summary>
public sealed class Net7ConnectionMeterTests
{
    private static byte[] Stamp(params uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    [Fact]
    public void TheMatchingAnswerGivesTheRoundTrip()
    {
        var meter = new Net7ConnectionMeter();
        meter.Sent(0x0044, Stamp(1000), 20);
        Assert.NotNull(meter.Sample().PendingMilliseconds);
        meter.TimeAnswered(Stamp(1000, 50, 51), 1137);
        var sample = meter.Sample();
        Assert.Equal(137, sample.LatencyMilliseconds);
        Assert.Null(sample.PendingMilliseconds);
        Assert.Equal(20, sample.BytesSent);
    }

    [Fact]
    public void OtherAnswersAndOtherFramesAreNoMeasurement()
    {
        var meter = new Net7ConnectionMeter();
        meter.TimeAnswered(Stamp(500, 1, 2), 700); // login's answer: no request of ours waits
        meter.Sent(0x0006, Stamp(1000), 20);       // not a time request
        meter.Sent(0x0044, Stamp(2000), 20);
        meter.TimeAnswered(Stamp(1999, 1, 2), 2100); // a different send time
        meter.Received(64);
        var sample = meter.Sample();
        Assert.Null(sample.LatencyMilliseconds);
        Assert.NotNull(sample.PendingMilliseconds);
        Assert.Equal(64, sample.BytesReceived);
        Assert.Equal(40, sample.BytesSent);
    }

    [Theory]
    [InlineData(null, null, 0)]
    [InlineData(120L, null, 1)]
    [InlineData(251L, null, 1)]
    [InlineData(500L, null, 2)]
    [InlineData(900L, null, 3)]
    [InlineData(120L, 2500L, 2)]
    [InlineData(120L, 6000L, 3)]
    [InlineData(120L, 40000L, 1)] // a lost answer no longer colours it
    [InlineData(null, 2500L, 2)]
    public void Levels(long? latency, long? waiting, int level)
        => Assert.Equal(level, EnbModernHud.Level(new(latency, 0, waiting, 0, 0, 0)));
}
