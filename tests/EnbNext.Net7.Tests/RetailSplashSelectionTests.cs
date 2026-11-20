using EnbNext.Engine;

namespace EnbNext.Net7.Tests;

public sealed class RetailSplashSelectionTests
{
    [Fact]
    public void EpochSecondsCycleAcrossAllSevenRetailSplashModels()
    {
        string[] expected =
        [
            "install00a", "install01a", "install02a", "install03a",
            "install04a", "install05a", "install06a", "install00a"
        ];

        Assert.Equal(expected, Enumerable.Range(0, expected.Length)
            .Select(second => RetailSplashSelection.ModelAt(second)));
    }
}
