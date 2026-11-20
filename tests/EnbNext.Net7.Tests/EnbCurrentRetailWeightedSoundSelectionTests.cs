using EnbNext.Assets;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbCurrentRetailWeightedSoundSelectionTests
{
    [Fact]
    public void EveryTicketPreservesAuthoredOrderAndNullWeight()
    {
        EnbRetailSoundFileChoice[] choices = [new("a", 2, false), new(null, 3, true), new("b", 1, false)];
        int[] expected = [0, 0, 1, 1, 1, 2];
        for (uint sample = 0; sample < 6; sample++)
        {
            int calls = 0;
            Assert.Equal(expected[sample], EnbCurrentRetailWeightedSoundSelection.NextChoice(choices, () => { calls++; return sample; }));
            Assert.Equal(1, calls);
        }
    }

    [Fact]
    public void RejectionUsesMaskedSamplesAndConsumesUntilAccepted()
    {
        EnbRetailSoundFileChoice[] choices = [new("a", 2, false), new(null, 3, true), new("b", 1, false)];
        var samples = new Queue<uint>([0xffffffff, 6, 0x80000005]);
        Assert.Equal(2, EnbCurrentRetailWeightedSoundSelection.NextChoice(choices, samples.Dequeue));
        Assert.Empty(samples);
    }

    [Fact]
    public void WideWeightsReachChoicesAboveFifteenBits()
    {
        EnbRetailSoundFileChoice[] choices = [new("a", 65536, false), new("b", 1, false)];
        Assert.Equal(1, EnbCurrentRetailWeightedSoundSelection.NextChoice(choices, () => 65536));
        Assert.Equal(0, EnbCurrentRetailWeightedSoundSelection.NextChoice(choices, () => 65535));
    }

    [Fact]
    public void SingleTicketAndDirectFilenameConsumeNoRandomness()
    {
        uint Never() => throw new InvalidOperationException("Unexpected random draw");
        Assert.Equal(0, EnbCurrentRetailWeightedSoundSelection.NextChoice([new("direct", null, false)], Never));
        Assert.Equal(0, EnbCurrentRetailWeightedSoundSelection.NextChoice([new(null, 1, true)], Never));
    }

    [Fact]
    public void InvalidOrOverflowingWeightsFailBeforeSampling()
    {
        uint Never() => throw new InvalidOperationException("Unexpected random draw");
        Assert.Throws<ArgumentException>(() => EnbCurrentRetailWeightedSoundSelection.NextChoice([], Never));
        Assert.Throws<ArgumentException>(() => EnbCurrentRetailWeightedSoundSelection.NextChoice([new("a", 0, false)], Never));
        Assert.Throws<ArgumentException>(() => EnbCurrentRetailWeightedSoundSelection.NextChoice([new("a", null, false), new("b", 1, false)], Never));
        Assert.Throws<OverflowException>(() => EnbCurrentRetailWeightedSoundSelection.NextChoice([new("a", int.MaxValue, false), new("b", 1, false)], Never));
    }

    [Fact]
    public void LargestSupportedTotalPreservesFinalBoundary()
    {
        EnbRetailSoundFileChoice[] choices = [new("a", int.MaxValue - 1, false), new("b", 1, false)];
        var samples = new Queue<uint>([0x7fffffff, 0x7ffffffe]);
        Assert.Equal(1, EnbCurrentRetailWeightedSoundSelection.NextChoice(choices, samples.Dequeue));
        Assert.Empty(samples);
    }
}
