using System.Numerics;
using EnbNext.Engine;
using EnbNext.Net7.LocalData;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class WaypointCommandTests
{
    [Theory]
    [InlineData("12.5 -3", null, 12.5f, -3f, 0f, "")]
    [InlineData("12.5, -3, 4 ore field", null, 12.5f, -3f, 4f, "ore field")]
    [InlineData("\"Mars Alpha\" 1 2", "Mars Alpha", 1f, 2f, 0f, "")]
    [InlineData("Mars Alpha, 1 2 3 gate", "Mars Alpha", 1f, 2f, 3f, "gate")]
    [InlineData("Earth 10 20", "Earth", 10f, 20f, 0f, "")]
    [InlineData("Mars Alpha 10 20 spot", "Mars Alpha", 10f, 20f, 0f, "spot")]
    [InlineData("10 20 my, spot", null, 10f, 20f, 0f, "my spot")]
    public void ParsesOptionalSectorCoordinatesAndLabel(string arguments, string? sector, float x, float y, float z, string label)
    {
        var command = EnbWayCommand.Parse(arguments);
        Assert.Null(command.Error);
        Assert.False(command.Clear);
        Assert.Equal(sector, command.Sector);
        Assert.Equal(new Vector3(x, y, z), command.Kilometres);
        Assert.Equal(label, command.Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Earth")]
    [InlineData("12")]
    [InlineData("\"Mars Alpha 1 2")]
    public void RejectsIncompleteArguments(string arguments) => Assert.NotNull(EnbWayCommand.Parse(arguments).Error);

    [Fact]
    public void ClearAndCurrentSectorAndNamedSectorDriveTheSingleWaypoint()
    {
        Assert.True(EnbWayCommand.Parse("clear").Clear);
        var galaxy = Net7LocalDataAssets.CurrentGalaxyMap;
        var named = galaxy.Sectors.First(s => s.Name.Contains(' '));
        var current = galaxy.Sectors.First(s => s.SectorHashId != named.SectorHashId);
        var book = new EnbWaypointBook();
        EnbWaypointCommand.Execute("/way 1.5 -2", book, current.SectorHashId, galaxy);
        Assert.Equal(new EnbWaypoint(current.SectorHashId, current.Name, new(1500, -2000, 0), ""), book.Active);
        EnbWaypointCommand.Execute($"/way \"{named.Name}\" 3 4 5 target", book, current.SectorHashId, galaxy);
        Assert.Equal(new EnbWaypoint(named.SectorHashId, named.Name, new(3000, 4000, 5000), "target"), book.Active);
        EnbWaypointCommand.Execute("/way clear", book, current.SectorHashId, galaxy);
        Assert.Null(book.Active);
        Assert.StartsWith("No current sector", EnbWaypointCommand.Execute("/way 1 2", book, null, galaxy));
        Assert.StartsWith("Unknown sector", EnbWaypointCommand.Execute("/way \"No Such Sector Anywhere\" 1 2", book, null, galaxy));
    }

    [Theory]
    [InlineData(0, 10000, 0)]
    [InlineData(6400, 12900, 1)]
    [InlineData(-6400, 12900, -1)]
    [InlineData(400, 600, 0)]
    [InlineData(800, 100000, 0)]
    [InlineData(-9000, 100000, -1)]
    public void LevelMarkClimbsDescendsOrStaysLevel(double dz, double distance, int level)
        => Assert.Equal(level, EnbWaypointArrow.Level(dz, distance));

    [Fact]
    public void MarkersAreSharedNotesAndTheArrowFollowsEditsAndDeletes()
    {
        var book = new EnbWaypointBook();
        int saves = 0; book.SaveNotes = _ => saves++;
        var note = book.Add(7, "Sol", new(1, 2, 0), "  ", " rich ");
        Assert.Equal("Marker", note.Name);
        Assert.Equal("rich", note.Note);
        book.Activate(note);
        Assert.Equal(note.Id, book.Active!.NoteId);
        book.Update(note with { Name = "Ore" });
        Assert.Equal("Ore", book.Active!.Label);
        Assert.Single(book.InSector(7));
        Assert.Empty(book.InSector(8));
        book.Remove(note.Id);
        Assert.Null(book.Active);
        Assert.Empty(book.Notes);
        Assert.Equal(3, saves);
    }
}
