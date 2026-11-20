using EnbNext.Assets;
using EnbNext.Engine;

namespace EnbNext.Net7.Tests;

public sealed class EnbMusicPlayerTests
{
    private static EnbRetailMusicTheme Theme(int id, int race, string type, int sector = -1, params string[] files)
        => new(id, race, type, 0, sector, 0, files.Select(f => new EnbRetailMusicComponent(f, 10, 10)).ToArray());

    private static EnbRetailMusicCatalog Catalog() => new([
        Theme(0, 0, "Wander", -1, "TerranWander01-1.mp3", "TerranWander01-2.mp3"),
        Theme(1, 1, "Wander", -1, "JenquaiWander01.mp3"),
        Theme(2, 2, "Combat", -1, "ProgenCombat03-1.mp3"),
        Theme(3, 8, "Wander", 4520, "Carpenter.mp3"),
        Theme(4, 8, "Planet", -1, "null.wav")], 20, 60);

    [Theory]
    [InlineData("TerranWander01-2.mp3", "Terran Wander 1, part 2")]
    [InlineData("JenquaiWander01.mp3", "Jenquai Wander 1")]
    [InlineData("ProgenCombat03-1.mp3", "Progen Combat 3, part 1")]
    [InlineData("Carpenter.mp3", "Carpenter")]
    public void PieceNamesReadAsWords(string file, string name) => Assert.Equal(name, EnbMusicPlayer.PieceName(file));

    [Fact]
    public void TheLibraryListsEveryComponentByRaceAndSkipsSilence()
    {
        var own = new[] { Theme(-1, 8, "Wander", -1, Path.Combine(Path.GetTempPath(), "Music", "Mine.mp3")) };
        var library = EnbMusicPlayer.BuildLibrary(Catalog(), own);
        Assert.Equal(["Terran Wander 1, part 1", "Terran Wander 1, part 2", "Jenquai Wander 1", "Progen Combat 3, part 1", "Carpenter", "Mine"], library.Select(t => t.Name));
        Assert.Equal(["Terran", "Terran", "Jenquai", "Progen", "Sector themes", "Your music"], library.Select(t => t.Group));
        Assert.All(library, t => Assert.Single(t.Theme.Components));
        Assert.True(library[^1].Own);
        Assert.True(library[1].Same(Catalog().Themes[0] with { Components = [Catalog().Themes[0].Components[1]] }));
    }

    [Fact]
    public void LeftAloneTheRacesAreRetailsAndTheLastRaceStays()
    {
        var player = new EnbMusicPlayer { OwnRace = 1 };
        Assert.True(player.RaceIncluded(1)); Assert.True(player.RaceIncluded(8)); Assert.False(player.RaceIncluded(0));
        player.ToggleRace(0);
        Assert.Equal(0b011, player.Races); Assert.True(player.RaceIncluded(0)); Assert.True(player.RaceIncluded(1));
        player.ToggleRace(1);
        Assert.Equal(0b001, player.Races); Assert.False(player.RaceIncluded(1)); Assert.True(player.RaceIncluded(8));
        player.ToggleRace(0);
        Assert.Equal(0b001, player.Races);
        player.ToggleRace(1); player.ToggleRace(0);
        // Back to only the character's own race: retail's choice again.
        Assert.Equal(0, player.Races);
    }

    [Fact]
    public void ThePlaylistFollowsTheChosenRaces()
    {
        var catalog = Catalog();
        int First(int low, int high) => low;
        var player = new EnbMusicPlayer { OwnRace = 0 };
        Assert.Equal([0], catalog.Playlist("Wander", player.RaceIncluded, 50, null, First).Select(t => t.Id));
        player.ToggleRace(1);
        Assert.Equal([0, 1], catalog.Playlist("Wander", player.RaceIncluded, 50, null, First).Select(t => t.Id).Order());
        // The race overload is the retail one when nothing is chosen.
        Assert.Equal(catalog.Playlist("Wander", 0, 50, null, First).Select(t => t.Id), new EnbMusicPlayer { OwnRace = 0 } is var retail ? catalog.Playlist("Wander", retail.RaceIncluded, 50, null, First).Select(t => t.Id) : []);
    }

    [Fact]
    public void TheFolderGivesWanderCombatAndPlanetPieces()
    {
        string folder = Path.Combine(Path.GetTempPath(), "enbnext-music-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "Combat"));
            File.WriteAllBytes(Path.Combine(folder, "b.mp3"), []); File.WriteAllBytes(Path.Combine(folder, "a.wav"), []);
            File.WriteAllBytes(Path.Combine(folder, "notes.txt"), []); File.WriteAllBytes(Path.Combine(folder, "Combat", "fight.mp3"), []);
            var (themes, error) = EnbMusicPlayer.LoadFolder(folder);
            Assert.Null(error);
            Assert.Equal(["a.wav", "b.mp3", "fight.mp3"], themes.Select(t => Path.GetFileName(t.Components[0].ResourceName)));
            Assert.Equal(["Wander", "Wander", "Combat"], themes.Select(t => t.Type));
            Assert.All(themes, t => { Assert.True(t.Id < 0); Assert.Equal(8, t.Race); Assert.True(Path.IsPathFullyQualified(t.Components[0].ResourceName)); });
            Assert.Empty(EnbMusicPlayer.LoadFolder(Path.Combine(folder, "missing")).Themes);
        }
        finally { Directory.Delete(folder, true); }
    }
}
