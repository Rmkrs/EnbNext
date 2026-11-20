using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbNavigationMapStateTests
{
    [Fact]
    public void DefaultsAndAuthoredSizeAnimation()
    {
        var state = new EnbNavigationMapState(new(false));
        Assert.Equal(59, state.Frame);
        state.ToggleSize(); state.Advance(225);
        Assert.Equal(44, state.Frame, 4);
        state.Advance(225); Assert.Equal(29, state.Frame, 4);
        state.Advance(10000); Assert.Equal(29, state.Frame);
        state.ToggleSize(); state.Advance(10000); Assert.Equal(59, state.Frame);
        state.Advance(float.NaN); Assert.Equal(59, state.Frame);
    }

    [Fact]
    public void SavedTypesAreRespectedAndSizeEndpointsAreValidated()
    {
        var options = EnbRetailPlayerOptionsCatalog.ParseText("""
            [FLOAT-1]
            OptionName=Starmap_MinFrame
            OptionValue=53
            [FLOAT-2]
            OptionName=Starmap_MaxFrame
            OptionValue=23
            [BOOL-3]
            OptionName=Starmap_Maximized
            OptionValue=no
            [INT-4]
            OptionName=Starmap_Label
            OptionValue=3
            [INT-5]
            OptionName=Starmap_ShowCoords
            OptionValue=2
            """);
        var state = new EnbNavigationMapState(options);
        Assert.Equal(23,state.MinimumFrame); Assert.Equal(53,state.MaximumFrame);
        Assert.Equal(23,state.Frame); Assert.Equal(3,state.LabelMode); Assert.Equal(2,state.CoordinateMode);
        var invalid = new EnbNavigationMapState(options with { StarmapMinFrame = -1, StarmapMaxFrame = float.PositiveInfinity });
        Assert.Equal(29,invalid.MinimumFrame); Assert.Equal(59,invalid.MaximumFrame);
    }

    [Fact]
    public void LabelsAndCoordinatesHaveIndependentCycles()
    {
        var state = new EnbNavigationMapState(new(false, StarmapLabel:0));
        Assert.False(state.ShowsLabel(true,true,true));
        state.CycleLabels(); Assert.True(state.ShowsLabel(true,false,true));
        Assert.True(state.ShowsLabel(false,true,true)); Assert.False(state.ShowsLabel(false,false,true));
        state.CycleLabels(); Assert.True(state.ShowsLabel(false,false,true)); Assert.False(state.ShowsLabel(false,false,false));
        state.CycleLabels(); Assert.True(state.ShowsLabel(false,false,false));
        state.CycleLabels(); Assert.Equal(0,state.LabelMode);
        state.CycleCoordinates(); Assert.Equal(1,state.CoordinateMode);
        state.CycleCoordinates(); Assert.Equal(2,state.CoordinateMode);
        state.CycleCoordinates(); Assert.Equal(0,state.CoordinateMode);
    }
}
