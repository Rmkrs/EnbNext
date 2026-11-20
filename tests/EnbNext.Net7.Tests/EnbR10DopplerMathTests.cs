using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10DopplerMathTests
{
    [Theory]
    [InlineData(1, 1.33333333f)]
    [InlineData(-1, .8f)]
    [InlineData(0, 1)]
    public void ApproachAndRecedeHaveOppositePitchWithRetailMotionFilter(float velocity, float expected)
    {
        var step = Step(new(velocity, 0, 0));
        Assert.Equal(expected, step.Pitch, 5);
        Assert.Equal(new Vector3(Math.Abs(velocity) * .85f, 0, 0), step.FilteredMotion);
    }

    [Fact]
    public void SharedMotionAndPerpendicularMotionDoNotShiftPitch()
    {
        var movingTogether = EnbCurrentRetailDopplerMath.Step(new(1, 2, 3), new(1, 2, 3), Vector3.UnitX, Vector3.Zero, 1, 16, 1, .5f, 2);
        Assert.Equal(1, movingTogether.Pitch);
        Assert.Equal(Vector3.Zero, movingTogether.FilteredMotion);
        Assert.Equal(1, Step(Vector3.UnitY).Pitch);
        var coincident = EnbCurrentRetailDopplerMath.Step(Vector3.UnitX, Vector3.Zero, Vector3.Zero, Vector3.Zero, 1, 16, 1, .5f, 2);
        Assert.Equal(1, coincident.Pitch);
    }

    [Fact]
    public void SlewUsesMillisecondsThenAppliesAbsolutePitchBounds()
    {
        var shortFrame = Step(Vector3.UnitX, elapsed: 16);
        Assert.Equal(1 + 16 * .05f * .03003003f, shortFrame.Pitch, 6);
        Assert.Equal(1.1f, Step(Vector3.UnitX, maximum: 1.1f).Pitch);
        Assert.Equal(.9f, Step(-Vector3.UnitX, minimum: .9f).Pitch);
        Assert.Equal(1, Step(Vector3.UnitX, elapsed: 0).Pitch);
    }

    [Fact]
    public void FilteringRetainsVectorHistoryAcrossTurns()
    {
        var first = Step(Vector3.UnitX);
        var turned = EnbCurrentRetailDopplerMath.Step(Vector3.UnitY, Vector3.Zero, Vector3.UnitY, first.FilteredMotion, first.Pitch, 1000, 1, .5f, 2);
        Assert.Equal(.1275f, turned.FilteredMotion.X, 6);
        Assert.Equal(.85f, turned.FilteredMotion.Y, 6);
        Assert.True(turned.Pitch > first.Pitch);
    }

    [Fact]
    public void AuthoredTweakAndZeroDenominatorBranchArePreserved()
    {
        Assert.Equal(1, Step(Vector3.UnitX, tweak: 0).Pitch);
        Assert.InRange(Step(Vector3.UnitX, tweak: .1f).Pitch, 1, Step(Vector3.UnitX).Pitch);
        Assert.Equal(3.4f, Step(new(4, 0, 0), maximum: 4, elapsed: 10000).DesiredPitch);
        Assert.Equal(.5f, Step(new(-4, 0, 0)).DesiredPitch);
    }

    [Fact]
    public void NonFiniteAndUnorderedInputsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Step(new(float.NaN, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Step(Vector3.Zero, elapsed: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Step(Vector3.Zero, minimum: 2, maximum: 1));
    }

    private static EnbCurrentRetailDopplerStep Step(Vector3 velocity, float elapsed = 1000, float minimum = .5f, float maximum = 2, float tweak = 1)
        => EnbCurrentRetailDopplerMath.Step(velocity, Vector3.Zero, Vector3.UnitX, Vector3.Zero, 1, elapsed, tweak, minimum, maximum);
}
