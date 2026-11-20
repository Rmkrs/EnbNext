using System.Numerics;
using EnbNext.Engine;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

/// <summary>Next click-to-walk in stations: the walking grid keeps to the middle of halls and away from people.</summary>
public sealed class EnbStationNavigationTests
{
    private static void Quad(List<(Vector3, Vector3, Vector3, bool)> flat, float x0, float y0, float x1, float y1, float z)
    {
        flat.Add((new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), true));
        flat.Add((new(x0, y0, z), new(x1, y1, z), new(x0, y1, z), true));
    }

    // Two 30 by 30 rooms joined by a 10 wide hall, a table in the first room.
    private static EnbStationNavigation TwoRooms()
    {
        var flat = new List<(Vector3, Vector3, Vector3, bool)>();
        Quad(flat, 0, 0, 30, 30, 0);
        Quad(flat, 30, 10, 70, 20, 0);
        Quad(flat, 70, 0, 100, 30, 0);
        Quad(flat, 10, 2, 20, 8, 3); // The table top: walkable only around it.
        return EnbStationNavigation.Build(flat, 0);
    }

    [Fact]
    public void PathKeepsToTheMiddleOfTheHall()
    {
        var grid = TwoRooms();
        var path = grid.FindPath(new(5, 25), new(95, 5), 1.5f, []);
        Assert.NotNull(path);
        Assert.True(Vector2.Distance(path![^1], new(95, 5)) <= 1.5f);
        foreach (var p in path.Where(p => p.X > 35 && p.X < 65)) Assert.InRange(p.Y, 14, 16);
        foreach (var p in path)
        {
            Assert.True(grid.TryCell(p, out int x, out int y));
            Assert.True(grid.ClearanceAt(x, y) >= EnbStationNavigation.Radius);
        }
    }

    [Fact]
    public void PathGoesAroundTablesAndPeople()
    {
        var grid = TwoRooms();
        // The table stands between (the gap below it is too narrow): the way goes around above it, never over it.
        var path = grid.FindPath(new(4, 5), new(26, 5), 1, []);
        Assert.NotNull(path);
        Assert.Contains(path!, p => p.Y > 9.5f);
        Assert.DoesNotContain(path!, p => grid.TryCell(p, out int x, out int y) && grid.Obstructed(x, y));
        Assert.True(grid.TryCell(new(15, 5), out int tx, out int ty) && grid.Obstructed(tx, ty) && !grid.Walkable(tx, ty));
        // Someone standing in the hall: the way keeps clear of them.
        var person = new Vector2(50, 15);
        var around = grid.FindPath(new(5, 15), new(95, 15), 1.5f, [person], 2);
        Assert.NotNull(around);
        Assert.All(around!, p => Assert.True(Vector2.Distance(p, person) >= 2));
    }

    private static void Wall(List<(Vector3, Vector3, Vector3, bool)> flat, float x0, float y0, float x1, float y1)
    {
        flat.Add((new(x0, y0, 0), new(x1, y1, 0), new(x1, y1, 5), true));
        flat.Add((new(x0, y0, 0), new(x1, y1, 5), new(x0, y0, 5), true));
    }

    [Fact]
    public void WallsBlockAndTheWayGoesThroughTheMiddleOfAGap()
    {
        // One floor with a wall across it, an eight wide doorway in the wall (as door frames stand on a floor running through).
        var flat = new List<(Vector3, Vector3, Vector3, bool)>();
        Quad(flat, 0, 0, 60, 30, 0);
        Wall(flat, 30, 0, 30, 11); Wall(flat, 30, 19, 30, 30);
        var grid = EnbStationNavigation.Build(flat, 0);
        Assert.True(grid.TryCell(new(30.2f, 5), out int wx, out int wy) && grid.Obstructed(wx, wy));
        var path = grid.FindPath(new(10, 3), new(50, 27), 1.5f, []);
        Assert.NotNull(path);
        Assert.All(path!.Where(p => MathF.Abs(p.X - 30) < 1.5f), p => Assert.InRange(p.Y, 13.5f, 16.5f));
    }

    [Fact]
    public void BridgesOverLowerGroundAreWalkedAndItsEdgesKeptOff()
    {
        // Two floors joined by an eight wide bridge over a pit five lower (as the Bazaar's walkway).
        var flat = new List<(Vector3, Vector3, Vector3, bool)>();
        Quad(flat, 0, 0, 20, 20, 0); Quad(flat, 40, 0, 60, 20, 0);
        Quad(flat, 20, 0, 40, 20, -5); Quad(flat, 20, 6, 40, 14, 0);
        var grid = EnbStationNavigation.Build(flat, 0);
        var path = grid.FindPath(new(5, 10), new(55, 10), 1.5f, []);
        Assert.NotNull(path);
        Assert.All(path!.Where(p => p.X > 21 && p.X < 39), p => Assert.InRange(p.Y, 9, 11));
        // The pit is out of reach: no way into it, but as close as it goes when asked.
        Assert.Null(grid.FindPath(new(5, 10), new(30, 2), 1.5f, []));
        var closest = grid.FindPath(new(5, 10), new(30, 2), 1.5f, [], closest: true);
        Assert.NotNull(closest);
        Assert.InRange(closest![^1].X, 27, 33);
        Assert.InRange(closest[^1].Y, 9, 10);
    }

    [Fact]
    public void NoWayWhenTheHallIsBlocked()
    {
        var grid = TwoRooms();
        Assert.Null(grid.FindPath(new(5, 15), new(95, 15), 1.5f, [new(50, 12), new(50, 18)], 4));
    }

    [Fact]
    public void StationMapPutsTheHangarBelowTheMainDeckHallToHall()
    {
        var flat = new List<(Vector3, Vector3, Vector3, bool)>();
        Quad(flat, -20, -20, 20, 20, 0); Quad(flat, -30, -4, -20, 4, 0); // Main Deck, its hall to the Hangar going -X.
        var deck = new EnbStationMapRoom(1, EnbStationNavigation.Build(flat, 0), [], [new(0, new(-20, 0), -Vector2.UnitX, 10)]);
        flat.Clear();
        Quad(flat, 0, 0, 40, 40, 0); Quad(flat, 40, 16, 55, 24, 0); // Hangar, its hall to the Main Deck going +X.
        var hangar = new EnbStationMapRoom(0, EnbStationNavigation.Build(flat, 0), [], [new(1, new(40, 20), Vector2.UnitX, 15)]);
        var map = EnbWorldRenderer.ComposeStationMap([hangar, deck]);
        Assert.NotNull(map);
        var deckPlace = map!.Rooms.Single(r => r.Room.Room == 1);
        var hangarPlace = map.Rooms.Single(r => r.Room.Room == 0);
        // The Main Deck turns so its door to the Hangar points down; the Hangar's hall meets it from below.
        var down = deckPlace.ToMap(new(-21, 0)) - deckPlace.ToMap(new(-20, 0));
        Assert.True(Vector2.Distance(down, -Vector2.UnitY) < 1e-3f);
        var deckEnd = deckPlace.ToMap(new(-30, 0));
        var hangarEnd = hangarPlace.ToMap(new(55, 20));
        Assert.True(Vector2.Distance(deckEnd, hangarEnd) < 1e-2f);
        Assert.True(hangarPlace.LabelAt.Y < deckPlace.LabelAt.Y);
        Assert.True(map.Floor.Width > 0 && map.Floor.Height > map.Floor.Width);
    }
}
