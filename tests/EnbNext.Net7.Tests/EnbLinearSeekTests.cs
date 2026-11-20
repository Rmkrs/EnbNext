using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbLinearSeekTests
{
    [Fact]
    public void LongTravelBuildsEveryKnotWithoutACountLimit()
    {
        // 006F1590/006F1C40 append duration/change+1 knots with no limit: 20 minutes at 100 ms is 12001 knots.
        var linear = new EnbRetailLinearSeekEffectPath(1_200_000, 100, 100, 25, 7);
        var spline = new EnbRetailSeekEffectPath(1_200_000, 100, 25, false, 7);
        Assert.InRange(MathF.Abs(linear.Sample(1_200_000, Vector3.Zero, new(1000, 0, 0)).Translation.X - 1000), 0, .01f);
        Assert.InRange(MathF.Abs(spline.Sample(1_200_000, Vector3.Zero, new(1000, 0, 0)).Translation.X - 1000), 0, .01f);
    }
    [Fact]
    public void PairedKeysHoldLateralOffsetAndPreserveAsymmetricFinalInterval()
    {
        // D=2000,C=650,T=250: K=4,G=1000/3. Interior pairs begin at
        // 250 and 833.333ms; their holds end at 583.333 and 1166.667ms.
        var path = new EnbRetailLinearSeekEffectPath(2000, 650, 250, 25, 7);
        var start = path.Sample(250, Vector3.Zero, new(2000, 0, 0)).Translation;
        var hold = path.Sample(500, Vector3.Zero, new(2000, 0, 0)).Translation;
        Assert.InRange(MathF.Abs(start.Y - hold.Y), 0, .0001f);
        Assert.InRange(MathF.Abs(start.Z - hold.Z), 0, .0001f);
        Assert.InRange(MathF.Sqrt(start.Y * start.Y + start.Z * start.Z), 24.999f, 25.001f);
        var lastHold = path.Sample(1000, Vector3.Zero, new(2000, 0, 0)).Translation;
        var tail = path.Sample(1500, Vector3.Zero, new(2000, 0, 0)).Translation;
        Assert.InRange(MathF.Abs(tail.Y - lastHold.Y * .6f), 0, .001f);
        Assert.InRange(MathF.Abs(tail.Z - lastHold.Z * .6f), 0, .001f);
        Assert.InRange(MathF.Abs(tail.X - 1500), 0, .001f);
    }

    [Fact]
    public void ShortDurationUsesSingleInteriorKeyAndScaledRadius()
    {
        var path = new EnbRetailLinearSeekEffectPath(500, 650, 250, 26, 9);
        var midpoint = path.Sample(250, Vector3.Zero, new(500, 0, 0)).Translation;
        Assert.InRange(MathF.Sqrt(midpoint.Y * midpoint.Y + midpoint.Z * midpoint.Z), 9.999f, 10.001f);
        var quarter = path.Sample(125, Vector3.Zero, new(500, 0, 0)).Translation;
        Assert.InRange(Vector3.Distance(quarter, midpoint * .5f), 0, .001f);
        Assert.Equal(Vector3.Zero, path.Sample(0, Vector3.Zero, new(500, 0, 0)).Translation);
        Assert.Equal(new Vector3(500, 0, 0), path.Sample(900, Vector3.Zero, new(500, 0, 0)).Translation);
    }

    [Theory]
    [InlineData(749)]
    [InlineData(750)]
    [InlineData(751)]
    public void TimingBranchBoundaryRemainsFiniteAndContinuous(uint duration)
    {
        var path = new EnbRetailLinearSeekEffectPath(duration, 650, 250, 25, 1);
        var target = new Vector3(duration, 0, 0);
        for (uint age = 0; age <= duration; age++)
        {
            var p = path.Sample(age, Vector3.Zero, target).Translation;
            Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z));
            Assert.InRange(MathF.Abs(p.X - age), 0, .001f);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void OrientationUsesEndpointAxisAndSamplingIsRepeatable(int yaw)
    {
        var source = new Vector3(20, -40, 5);
        var axis = Vector3.TransformNormal(Vector3.UnitX, Matrix4x4.CreateRotationZ(yaw * MathF.PI / 180));
        var target = source + axis * 600;
        var path = new EnbRetailLinearSeekEffectPath(2000, 650, 250, 25, 17);
        for (uint age = 100; age < 2000; age += 113)
        {
            var pose = path.Sample(age, source, target);
            Assert.Equal(pose, path.Sample(age, source, target));
            Assert.InRange(Vector3.Distance(axis, new(pose.M11, pose.M12, pose.M13)), 0, .001f);
        }
    }

    [Fact]
    public void NegativeDistanceClampsAndInvalidDurationUsesOneMillisecond()
    {
        var path = new EnbRetailLinearSeekEffectPath(0, 0, 0, -1, 0);
        Assert.Equal(new Vector3(100, 0, 0), path.Sample(1, Vector3.Zero, new(100, 0, 0)).Translation);
        Assert.Throws<InvalidDataException>(() => new EnbRetailLinearSeekEffectPath(1000, 650, 250, float.NaN, 0));
    }
}
