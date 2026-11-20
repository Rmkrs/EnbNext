using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbBridgeCameraLookTests
{
    [Fact]
    public void InputUsesHalfSensitivityAndFinalBridgeLimits()
    {
        var look = new EnbBridgeCameraLook();
        Assert.True(look.TryAdjust(.1, .2, 20, true, false, true, 0, 0));
        Assert.Equal(-.1f, look.HeadingRadians, 6);
        Assert.Equal(.2f, look.TiltRadians, 6);
        look.TryAdjust(100, 100, 20, true, false, true, 0, 0);
        Assert.Equal(-.25f, look.HeadingRadians);
        Assert.Equal(.4f, look.TiltRadians);
        look.TryAdjust(-100, -100, 20, true, false, true, 0, 0);
        Assert.Equal(.25f, look.HeadingRadians);
        Assert.Equal(-.2f, look.TiltRadians);
    }

    [Theory]
    [InlineData(false, false, true, 0, 0)]
    [InlineData(true, true, true, 0, 0)]
    [InlineData(true, false, false, 0, 0)]
    [InlineData(true, false, true, 1, 1)]
    [InlineData(true, false, true, 0, 1)]
    [InlineData(true, false, true, 1, 0)]
    public void ModifierResourceAndTransitionGuardsRetainState(bool control, bool alt, bool bound, int state, int target)
    {
        var look = new EnbBridgeCameraLook();
        look.TryAdjust(.1, .2, 20, true, false, true, 0, 0);
        Assert.False(look.TryAdjust(1, 1, 20, control, alt, bound, state, target));
        Assert.Equal(-.1f, look.HeadingRadians, 6);
        Assert.Equal(.2f, look.TiltRadians, 6);
    }

    [Fact]
    public void PoseRotatesTranslationHeadingThenTiltAndRecoversAfterConsumption()
    {
        var look = new EnbBridgeCameraLook();
        look.TryAdjust(-.2, .3, 20, true, false, true, 0, 0);
        var authored = Matrix4x4.CreateRotationZ(.7f) * Matrix4x4.CreateTranslation(2, 3, -4);
        var first = look.ConsumeRetailTransform(authored, 0);
        // Independent scalar transcription of retail's 3x4 row operations.
        float x = 2 * MathF.Cos(.2f) - 4 * MathF.Sin(.2f);
        float z = -4 * MathF.Cos(.2f) - 2 * MathF.Sin(.2f);
        Assert.InRange(MathF.Abs(x - first.M41), 0, 1e-6f);
        Assert.InRange(MathF.Abs(3 * MathF.Cos(.3f) - z * MathF.Sin(.3f) - first.M42), 0, 1e-6f);
        Assert.InRange(MathF.Abs(z * MathF.Cos(.3f) + 3 * MathF.Sin(.3f) - first.M43), 0, 1e-6f);
        Assert.Equal(.2f, look.HeadingRadians, 6);
        Assert.Equal(first, look.ConsumeRetailTransform(authored, 0));
        Assert.Equal(.17f, look.HeadingRadians, 6);
        Assert.Equal(.255f, look.TiltRadians, 6);
        Assert.NotEqual(first, look.ConsumeRetailTransform(authored, 0));
        Assert.Equal(authored, look.ConsumeRetailTransform(authored, 1));
        look.Reset();
        Assert.Equal(authored, look.ConsumeRetailTransform(authored, 0));
    }

    [Fact]
    public void InvalidInputCannotPoisonCameraOrPostponeRecovery()
    {
        var look = new EnbBridgeCameraLook();
        look.TryAdjust(-.2, .3, 20, true, false, true, 0, 0);
        look.ConsumeRetailTransform(Matrix4x4.Identity, 0);
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.False(look.TryAdjust(invalid, 0, 20, true, false, true, 0, 0));
            Assert.False(look.TryAdjust(0, invalid, 20, true, false, true, 0, 0));
            Assert.False(look.TryAdjust(0, 0, invalid, true, false, true, 0, 0));
        }
        Assert.False(look.TryAdjust(0, 0, -1, true, false, true, 0, 0));
        look.ConsumeRetailTransform(Matrix4x4.Identity, 0);
        Assert.Equal(.17f, look.HeadingRadians, 6);
    }
}
