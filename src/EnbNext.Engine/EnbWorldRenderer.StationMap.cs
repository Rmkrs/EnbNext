using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>A labelled place in a room: a terminal, vendor or character (keyed as in Local) or a door.</summary>
internal readonly record struct EnbStationMapMark(string Key, string Name, EnbStationRosterKind Kind, Vector2 At);

/// <summary>A door of a room: where it stands, the way out through it, and how far its hall reaches beyond it.</summary>
internal readonly record struct EnbStationMapDoor(uint To, Vector2 At, Vector2 Out, float Reach);

/// <summary>One room for the station map and the click-to-walk: its walking grid (the same floor cells), places and doors.</summary>
internal sealed record EnbStationMapRoom(uint Room, EnbStationNavigation Navigation, ImmutableArray<EnbStationMapMark> Marks, ImmutableArray<EnbStationMapDoor> Doors);

/// <summary>Where a room lies on the whole-station map: rotated by (Cos, Sin), then moved by Offset; its name at LabelAt.</summary>
internal readonly record struct EnbStationMapPlacement(EnbStationMapRoom Room, float Cos, float Sin, Vector2 Offset, Vector2 LabelAt)
{
    public Vector2 ToMap(Vector2 local) => new(local.X * Cos - local.Y * Sin + Offset.X, local.X * Sin + local.Y * Cos + Offset.Y);
    public Vector2 ToRoom(Vector2 map) { var p = map - Offset; return new(p.X * Cos + p.Y * Sin, -p.X * Sin + p.Y * Cos); }
}

/// <summary>The whole station as one picture (north up: Min at the bottom left, one pixel per <see cref="PixelSize"/> units).</summary>
internal sealed record EnbStationMap(EnbUiRaster Floor, Vector2 Min, float PixelSize, ImmutableArray<EnbStationMapPlacement> Rooms)
{
    public Vector2 Max => Min + new Vector2(Floor.Width, Floor.Height) * PixelSize;
}

/// <summary>
/// Next Modern UI station map and click-to-walk (retail has neither). Each room's floor comes
/// from its own collision surfaces (EnbStationNavigation); its terminals, booths, characters and doors stand where the room
/// places them (the same pivots it draws them at). The map lays the rooms out as one station: the Main Deck turned so its
/// door to the Hangar points down, the Hangar below it, and every other room joined hall to hall at its door.
/// </summary>
internal sealed partial class EnbWorldRenderer
{
    internal static EnbStationMapRoom? BuildStationMapRoom(EnbCurrentRetailStationInterior room, EnbSceneStationDefinition definition, EnbRetailAssetBundle assets)
    {
        try
        {
            var marks = new List<EnbStationMapMark>();
            static Vector2 Flat(Matrix4x4 m) => new(m.M41, m.M42);
            bool Has(string pivot) => room.Resource.Hierarchies.Single().Pivots.Any(p => p.Name.Equals(pivot, StringComparison.OrdinalIgnoreCase));
            foreach (var fixture in StationFixturePlacements(room, definition))
            {
                const string prefix = "TERMINAL";
                if (!fixture.Key.StartsWith(prefix, StringComparison.Ordinal) || !fixture.Key.EndsWith("/monitor", StringComparison.Ordinal)) continue;
                uint slot = uint.Parse(fixture.Key.AsSpan(prefix.Length, 2), System.Globalization.CultureInfo.InvariantCulture);
                if (definition.Facilities.FirstOrDefault(f => f.RoomType == room.RoomType && f.Slot == slot) is { } facility && StationTerminalName(facility.Type) is { } name)
                    marks.Add(new($"terminal/{room.RoomType}/{slot}", name, EnbStationRosterKind.Terminal, Flat(fixture.World)));
            }
            foreach (var npc in definition.RoomNpcs(room.RoomType))
            {
                string pivot = $"AVATAR{npc.Slot:00}";
                if (string.IsNullOrWhiteSpace(npc.DisplayName) || !Has(pivot)) continue;
                var kind = npc.Variant == 0 && npc.Role < 8 ? EnbStationRosterKind.Vendor : EnbStationRosterKind.Character;
                marks.Add(new($"npc/{room.RoomType}/{npc.Slot}", npc.DisplayName, kind, Flat(room.PivotTransform(pivot))));
            }
            float level = room.Doorways.Length > 0 ? room.Doorways.Average(d => d.Transform.M43) : 0;
            // The fixtures movement collides with (EnbWorldRenderer.StationAvatar: all but icons and signs).
            var fixtures = new List<(EnbW3dRenderResource, Matrix4x4)>();
            var loaded = new Dictionary<string, EnbW3dRenderResource?>(StringComparer.OrdinalIgnoreCase);
            foreach (var fixture in StationFixturePlacements(room, definition))
            {
                if (fixture.Key.Contains("/icon/", StringComparison.Ordinal) || fixture.Key.EndsWith("/sign", StringComparison.Ordinal)) continue;
                if (!loaded.TryGetValue(fixture.Name, out var resource))
                {
                    try { resource = CompileStationFixture(fixture.Name, assets); } catch (InvalidDataException) { resource = null; }
                    loaded[fixture.Name] = resource;
                }
                if (resource is not null) fixtures.Add((resource, fixture.World));
            }
            var grid = EnbStationNavigation.Build(EnbStationCollision.Surfaces(room, fixtures), level);
            var doors = new List<EnbStationMapDoor>();
            foreach (var door in room.Doorways)
            {
                if (door.Destination is not uint to || !definition.Rooms.Any(r => r.RoomType == to)) continue;
                var at = Flat(door.Transform);
                marks.Add(new($"door/{room.RoomType}/{to}", "To " + StationRoomName(to), EnbStationRosterKind.Door, at));
                // The way out: the door's own axis that points away from the floor around it.
                Vector2 near = Vector2.Zero; int count = 0;
                for (int y = 0; y < grid.Height; y++)
                    for (int x = 0; x < grid.Width; x++)
                        if (!float.IsNaN(grid.FloorAt(x, y)) && Vector2.DistanceSquared(grid.CellCenter(x, y), at) < 25 * 25) { near += grid.CellCenter(x, y); count++; }
                var away = count > 0 ? at - near / count : Vector2.UnitX;
                var axes = new[] { new Vector2(door.Transform.M11, door.Transform.M12), new Vector2(door.Transform.M21, door.Transform.M22) };
                var axis = axes.Where(a => a.LengthSquared() > 1e-6f).Select(Vector2.Normalize).DefaultIfEmpty(Vector2.UnitX)
                    .MaxBy(a => MathF.Abs(Vector2.Dot(a, away)));
                var outward = Vector2.Dot(axis, away) >= 0 ? axis : -axis;
                // How far the hall beyond the door reaches.
                float reach = 0;
                for (int y = 0; y < grid.Height; y++)
                    for (int x = 0; x < grid.Width; x++)
                    {
                        if (float.IsNaN(grid.FloorAt(x, y))) continue;
                        var d = grid.CellCenter(x, y) - at;
                        float along = Vector2.Dot(d, outward), across = MathF.Abs(d.X * outward.Y - d.Y * outward.X);
                        if (across < 6) reach = MathF.Max(reach, along);
                    }
                doors.Add(new(to, at, outward, reach));
            }
            return new(room.RoomType, grid, [.. marks], [.. doors]);
        }
        catch (Exception e)
        {
            // The map and the walk are optional: a room they cannot use is left out.
            System.Diagnostics.Trace.WriteLine("Station map room " + room.RoomType + ": " + e.Message);
            return null;
        }
    }

    /// <summary>Lays the rooms out as one station and draws it.</summary>
    internal static EnbStationMap? ComposeStationMap(IReadOnlyList<EnbStationMapRoom> rooms)
    {
        try
        {
            if (rooms.Count == 0) return null;
            static (float Cos, float Sin) Turn(Vector2 from, Vector2 to)
            {
                float angle = MathF.Atan2(to.Y, to.X) - MathF.Atan2(from.Y, from.X);
                return (MathF.Cos(angle), MathF.Sin(angle));
            }
            var hub = rooms.FirstOrDefault(r => r.Room == 1) ?? rooms[0];
            var placements = new List<EnbStationMapPlacement>();
            static EnbStationMapDoor? Door(EnbStationMapRoom room, uint to)
            {
                foreach (var door in room.Doors) if (door.To == to && door.Out != default) return door;
                return null;
            }
            var (hc, hs) = Door(hub, 0) is { } down ? Turn(down.Out, -Vector2.UnitY) : (1f, 0f);
            var center = new EnbStationMapPlacement(hub, hc, hs, Vector2.Zero, default);
            placements.Add(center);
            foreach (var room in rooms)
            {
                if (room == hub) continue;
                if (Door(hub, room.Room) is not { } hubDoor || Door(room, hub.Room) is not { } roomDoor) continue;
                Vector2 hubOut = hubDoor.Out, roomOut = roomDoor.Out;
                // Hall end to hall end: the room's door faces back at the hub's door.
                var outward = center.ToMap(hubDoor.At + hubOut) - center.ToMap(hubDoor.At);
                var hubEnd = center.ToMap(hubDoor.At + hubOut * hubDoor.Reach);
                var (c, s) = Turn(roomOut, -outward);
                var turned = new EnbStationMapPlacement(room, c, s, Vector2.Zero, default);
                placements.Add(turned with { Offset = hubEnd - turned.ToMap(roomDoor.At + roomOut * roomDoor.Reach) });
            }
            // Bounds and each room's name point (the middle of its floor).
            var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
            for (int i = 0; i < placements.Count; i++)
            {
                var p = placements[i]; var grid = p.Room.Navigation;
                Vector2 sum = Vector2.Zero; int count = 0;
                for (int y = 0; y < grid.Height; y++)
                    for (int x = 0; x < grid.Width; x++)
                    {
                        if (float.IsNaN(grid.FloorAt(x, y))) continue;
                        var at = p.ToMap(grid.CellCenter(x, y));
                        min = Vector2.Min(min, at); max = Vector2.Max(max, at);
                        if (grid.Walkable(x, y)) { sum += at; count++; }
                    }
                placements[i] = p with { LabelAt = count > 0 ? sum / count : p.ToMap(grid.CellCenter(grid.Width / 2, grid.Height / 2)) };
            }
            min -= new Vector2(4); max += new Vector2(4);
            float size = MathF.Max(EnbStationNavigation.Cell, MathF.Max(max.X - min.X, max.Y - min.Y) / 900);
            int width = Math.Max(1, (int)MathF.Ceiling((max.X - min.X) / size)), height = Math.Max(1, (int)MathF.Ceiling((max.Y - min.Y) / size));
            // Each pixel: none (0), floor (1, with its height against the room's level) or something standing on it (2).
            var kind = new byte[width * height]; var rise = new float[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    var at = min + new Vector2((x + .5f) * size, (height - y - .5f) * size);
                    foreach (var p in placements)
                    {
                        var grid = p.Room.Navigation;
                        if (!grid.TryCell(p.ToRoom(at), out int gx, out int gy) || float.IsNaN(grid.FloorAt(gx, gy))) continue;
                        int i = y * width + x;
                        kind[i] = grid.Obstructed(gx, gy) ? (byte)2 : (byte)1;
                        rise[i] = grid.FloorAt(gx, gy) - grid.Level;
                        break;
                    }
                }
            var pixels = new EnbColor32[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    if (kind[i] == 0) continue;
                    bool edge = x == 0 || y == 0 || x == width - 1 || y == height - 1 || kind[i - 1] == 0 || kind[i + 1] == 0 || kind[i - width] == 0 || kind[i + width] == 0;
                    float h = Math.Clamp(rise[i] / 10f, -1, 1);
                    pixels[i] = edge ? new(150, 170, 215, 255)
                        : kind[i] == 2 ? new(96, 112, 150, 240)
                        : new((byte)(52 + 34 * h), (byte)(66 + 40 * h), (byte)(104 + 50 * h), 235);
                }
            return new(EnbUiRaster.FromPixels(width, height, pixels), min, size, [.. placements]);
        }
        catch (Exception e)
        {
            System.Diagnostics.Trace.WriteLine("Station map: " + e.Message);
            return null;
        }
    }
}
