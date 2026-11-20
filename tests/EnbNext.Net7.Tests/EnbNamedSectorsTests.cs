using EnbNext.Engine;

namespace EnbNext.Net7.Tests;

public sealed class EnbNamedSectorsTests
{
    private static readonly (uint, string)[] Sectors = [(1, "Earth"), (2, "High Earth"), (3, "Menorb"), (4, "Io"), (5, "Carpenter"), (6, "Witch Head Nebula"), (8, "Mercury"), (9, "Glory's Orbit"), (10, "The Valkyrie Twins")];

    [Fact]
    public void AJobsDetailsNameItsSector()
        => Assert.Equal([(3u, "Menorb")], EnbWorldUi.FindNamedSectors("Bring these parts to the trader at Menorb station, then return.", Sectors));

    [Fact]
    public void TheLongestNameWinsEachOnceInTheTextsOrder()
        => Assert.Equal([(6u, "Witch Head Nebula"), (2u, "High Earth")],
            EnbWorldUi.FindNamedSectors("Fly from the witch head nebula to High Earth, then back to the Witch Head Nebula.", Sectors));

    [Fact]
    public void OnlyWholeWordsAndShortNamesOnlyAsWritten()
    {
        Assert.Empty(EnbWorldUi.FindNamedSectors("Carpenters' io drives, Earthbound", Sectors));
        Assert.Equal([(4u, "Io")], EnbWorldUi.FindNamedSectors("Patrol the lanes around Io.", Sectors));
    }

    [Fact]
    public void ApostrophesAreTheGamesOwn()
        => Assert.Equal([(7u, "Freeman's Rest")], EnbWorldUi.FindNamedSectors("Meet him at Freeman’s Rest.", [(7, "Freemanâ\u0080\u0099s Rest")]));

    [Fact]
    public void ASectorInAStationsNameIsNotTheDestinationWhenAnotherIsNamed()
    {
        Assert.Equal([(9u, "Glory's Orbit")], EnbWorldUi.FindNamedSectors("Load the Prisoners at Mercury Station in Glory's Orbit.", Sectors));
        Assert.Equal([(8u, "Mercury")], EnbWorldUi.FindNamedSectors("Deliver the ore to Mercury Station.", Sectors));
    }

    [Fact]
    public void ALeadingTheMayBeLeftOut()
    {
        Assert.Equal([(10u, "The Valkyrie Twins")], EnbWorldUi.FindNamedSectors("Load the Prisoners at Aragoth Station in Valkyrie Twins.", Sectors));
        Assert.Equal([(10u, "The Valkyrie Twins")], EnbWorldUi.FindNamedSectors("Patrol the valkyrie  twins.", Sectors));
    }
}
