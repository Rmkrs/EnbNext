using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbNet7ProgressionTests
{
    [Theory]
    [InlineData(0, 0f, 0, 10_000)]
    [InlineData(10, .5f, 23_750, 47_500)]
    [InlineData(42, .4f, 105_000, 262_500)]
    [InlineData(49, 1f, 402_500, 402_500)]
    [InlineData(50, .2f, 84_500, 422_500)]
    [InlineData(57, .2f, 84_500, 422_500)]
    public void StepConvertsTheServerFractionToExperience(int level, float fraction, int earned, int required)
        => Assert.Equal((earned, required), EnbNet7Progression.Step(level, fraction));

    [Fact]
    public void UnknownLevelsAndFractionsGiveNoValues()
    {
        Assert.Null(EnbNet7Progression.Step(-1, .5f));
        Assert.Null(EnbNet7Progression.Step(5, float.NaN));
        Assert.Equal((0, 10_000), EnbNet7Progression.Step(0, -2f));
        Assert.Equal((10_000, 10_000), EnbNet7Progression.Step(0, 3f));
    }
}
