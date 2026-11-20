using EnbNext.Engine;

namespace EnbNext.Net7.Tests;

public sealed class EnbFactionKillReactionsTests
{
    /// <summary>The chart's "Net Change" row (Net-7 Wiki Improved Faction Chart): every column's sum.</summary>
    [Theory]
    [InlineData("Anseria", 0)] [InlineData("Bogeril", 1)] [InlineData("PW", .5)] [InlineData("CHVZ", -.5)] [InlineData("PT", 1)]
    [InlineData("TW", -.5)] [InlineData("GC", -1)] [InlineData("TE", -1)] [InlineData("TT", 0)] [InlineData("TerranPsi", -2.5)]
    [InlineData("PE", -1)] [InlineData("JE", -.5)] [InlineData("JT", -1.5)] [InlineData("JW", 0)] [InlineData("Alliance", -2)]
    [InlineData("Mordana", 1)] [InlineData("Pirate", .5)] [InlineData("V'Rix", 5)] [InlineData("PC", -.5)] [InlineData("TA", -.5)]
    public void EachColumnAddsUpToTheChartsNetChange(string killed, double net)
        => Assert.Equal(net, EnbFactionKillReactions.Net(killed), 3);

    [Fact]
    public void AFactionsDetailsAreItsColumnAndItsRow()
    {
        // What raises or lowers Centuriata Warriors (its row), the faction itself left out.
        var changing = EnbFactionKillReactions.Changing("PW");
        Assert.Equal(["JE", "Mordana", "Pirate", "V'Rix"], changing.Where(r => r.Multiplier > 0).Select(r => r.Killed).Order());
        Assert.Equal(["PE", "PT", "TT"], changing.Where(r => r.Multiplier < 0).Select(r => r.Killed).Order());
        Assert.DoesNotContain(changing, r => r.Killed == "PW");
        Assert.Contains(EnbFactionKillReactions.Changing("TW"), r => r.Killed == "Alliance" && r.Estimated);
    }
}
