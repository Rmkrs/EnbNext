using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbCurrentRetailMainViewPassTests
{
    [Theory]
    [InlineData(false, 0, true)] [InlineData(false, 1, true)] [InlineData(false, 5, true)]
    [InlineData(false, 2, false)] [InlineData(false, null, false)] [InlineData(true, 0, false)] [InlineData(true, 1, false)]
    public void SkySceneFollowsTheModeDispatchAndTheInitializingFlag(bool initializing, int? mode, bool expected)
        => Assert.Equal(expected, EnbCurrentRetailMainViewPassRuntime.RendersSkyScene(initializing, mode));

    [Theory]
    [InlineData(false, 1, false, false, true)]
    [InlineData(false, 1, true, false, false)]
    [InlineData(false, 1, false, true, false)]
    [InlineData(false, 0, false, false, false)]
    [InlineData(false, 5, false, false, false)]
    [InlineData(true, 1, false, false, false)]
    public void PlanetFogBackgroundIsPlanetModeOnlyWithoutSwapBackdropOrUnderwaterCamera(bool initializing, int? mode, bool swap, bool below, bool expected)
        => Assert.Equal(expected, EnbCurrentRetailMainViewPassRuntime.RendersPlanetFogBackground(initializing, mode, swap, below));

    [Theory]
    [InlineData(0, true)] [InlineData(1, false)] [InlineData(5, false)] [InlineData(null, false)]
    public void FourLightRigAndDayNightRunOnlyInModeZero(int? mode, bool expected)
        => Assert.Equal(expected, EnbCurrentRetailMainViewPassRuntime.UsesModeZeroLightRig(mode));

    [Theory]
    [InlineData(true, true, 2, 4, 2)]
    [InlineData(true, true, 7, 4, 3)]
    [InlineData(true, true, -1, 4, 0)]
    [InlineData(false, true, 2, 4, null)]
    [InlineData(true, false, 2, 4, null)]
    [InlineData(true, true, 2, 0, null)]
    public void AggregateChildTakesTheClampedParentLevelOnlyWhileAForcingParentRenders(bool force, bool renders, int parent, int count, int? expected)
        => Assert.Equal(expected, EnbCurrentRetailAggregateLevelRuntime.ResolvePushedLevel(force, renders, parent, count));
}
