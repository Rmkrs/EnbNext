using EnbNext.Assets;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbCurrentRetailIntegerRandomTests
{
    [Fact]
    public void StationTrafficIntervalUsesNativeFifteenBitSamples()
    {
        uint state = 1;
        // Independent arithmetic trace of 009D0050..0063 from seed 1.
        int[] expected = [69087, 48108, 64697, 46886, 61239, 72477, 70893, 56107];
        Assert.Equal(expected, expected.Select(_ => EnbCurrentRetailIntegerRandom.Next(ref state, 40000, 90000)));
        Assert.Equal(0x9cfbae39u, state);
    }

    [Fact]
    public void RejectionConsumesAdditionalGeneratorSteps()
    {
        uint state = 1;
        // For width 10, the first two masked samples are 15 and 12; third is 9.
        Assert.Equal(9, EnbCurrentRetailIntegerRandom.Next(ref state, 0, 10));
        Assert.Equal(0x2781e494u, state);
    }

    [Fact]
    public void EqualBoundsDoNotAdvanceAndReversedBoundsMatch()
    {
        uint state = 1, reversed = 1;
        Assert.Equal(40000, EnbCurrentRetailIntegerRandom.Next(ref state, 40000, 40000));
        Assert.Equal(1u, state);
        Assert.Equal(EnbCurrentRetailIntegerRandom.Next(ref state, 40000, 90000),
            EnbCurrentRetailIntegerRandom.Next(ref reversed, 90000, 40000));
        Assert.Equal(state, reversed);
    }

    [Fact]
    public void WideRangeMaskUsesLowFifteenBitsOfWidth()
    {
        uint state = 1;
        Assert.Equal(101, EnbCurrentRetailIntegerRandom.Next(ref state, 100, 32868));
        Assert.Equal(0x41c67ea6u, state);
    }

    [Fact]
    public void EntireSignedRangeSkipsSamplingThroughUnsignedOverflow()
    {
        uint state = 1;
        Assert.Equal(int.MinValue, EnbCurrentRetailIntegerRandom.Next(ref state, int.MinValue, int.MaxValue));
        Assert.Equal(1u, state);
    }

    [Fact]
    public void BothEndpointsAreReachableForSmallRange()
    {
        uint state = 1;
        Assert.Equal(1, EnbCurrentRetailIntegerRandom.Next(ref state, 0, 1));
        Assert.Equal(0, EnbCurrentRetailIntegerRandom.Next(ref state, 0, 1));
    }
}
