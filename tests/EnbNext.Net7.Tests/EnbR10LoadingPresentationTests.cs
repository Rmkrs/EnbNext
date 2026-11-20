using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10LoadingPresentationTests
{
    [Theory]
    [InlineData("", "", "", "", "", "")]
    [InlineData("Mars (Sol System)", "Sol", "Arx Magister", "Sol", "Now leaving Mars (Sol System)", "Entering Arx Magister (Sol)")]
    [InlineData("Arx Magister", "Sol", "Mars", "Sol", "Now leaving Arx Magister", "Entering Mars (Sol)")]
    [InlineData("Mars", "Sol", "Station", "", "Now leaving Mars", "Docking with Station")]
    [InlineData("", "Sol", "", "Vega", "Now leaving ", "Entering  (Vega)")]
    public void UsesExactRetailLoadingTextBranches(string source,string sourceSystem,string destination,string destinationSystem,
        string expectedSource,string expectedDestination)
    {
        var transition=new EnbSceneLocationTransition(1,source,sourceSystem,destination,destinationSystem);
        Assert.Equal(expectedSource,transition.SourceText);
        Assert.Equal(expectedDestination,transition.DestinationText);
    }
}
