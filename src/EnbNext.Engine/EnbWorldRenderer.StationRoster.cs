using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>What a station room holds for the Next Modern UI Local window: a player, a character, a vendor or a terminal (and, on the station map, a door).</summary>
internal enum EnbStationRosterKind { Player = 1, Character = 2, Vendor = 3, Terminal = 4, Door = 5 }

/// <summary>
/// One thing in the station: its room (wire room type) and whether that is the room the player stands in.
/// </summary>
internal readonly record struct EnbStationRosterEntry(string Key, string Name, EnbStationRosterKind Kind)
{
    public uint Room { get; init; }
    public bool InRoom { get; init; } = true;
    /// <summary>Where it stands, for the player's room (null elsewhere and when unknown).</summary>
    public Vector3? Position { get; init; }
}

/// <summary>
/// Where a walk to a roster entry in the player's room goes, and whether the avatar has it in reach: the range at which
/// clicking it works (12 for people and booths, 11 for terminals). A character or terminal that opens there is carried.
/// </summary>
internal readonly record struct EnbStationWalkGoal(Vector3 Position, bool InReach,
    Presentation.EnbSceneStationNpc? Npc = null, Presentation.EnbSceneStationFacility? Facility = null);

/// <summary>
/// Next Modern UI Local inside a station: everything the client knows to be there. The players the server placed in
/// the player's room, and every room's characters (a vendor is one standing at a booth: variant 0, booth role 0 to 7,
/// as 00634140 attaches booths) and terminals (named as the hover hint, 00639ABC) from the station definition. Things
/// in the player's room carry where they stand (for the walk and the station map).
/// The game shows no such list; this is a Next feature over data the room already draws.
/// </summary>
internal sealed partial class EnbWorldRenderer
{
    private EnbStationRosterEntry[] _stationRoster = [];
    private long _stationRosterBuilt = long.MinValue;
    /// <summary>The current room's roster (refreshed a few times a second while in a station; empty elsewhere).</summary>
    internal IReadOnlyList<EnbStationRosterEntry> StationRoster => Volatile.Read(ref _stationRoster);

    /// <summary>A room's name as its door label names it (00639630: To Hangar, To Main Deck, To Bazaar, To Lounge).</summary>
    internal static string StationRoomName(uint room) => room switch { 0 => "Hangar", 1 => "Main Deck", 2 => "Bazaar", 3 => "Lounge", _ => "" };

    /// <summary>The terminal names of the hover hint (00639ABC indexes B90BF8 by facility type).</summary>
    internal static string? StationTerminalName(uint type) => type switch
    {
        0 => "Refining", 1 => "Analyze", 2 => "Manufacturing", 3 => "Jobs", 4 => "Intergalactic Net",
        5 => "Customize Ship", 6 => "Customize Avatar", 7 => "Training", _ => null,
    };

    private void PublishStationRoster(EnbCurrentRetailStationInterior room, Presentation.EnbSceneStationDefinition definition)
    {
        // A few times a second (a new room's clock restarts, so an earlier stamp rebuilds at once).
        if (_stationRosterBuilt != long.MinValue && _stationElapsedMilliseconds >= _stationRosterBuilt && _stationElapsedMilliseconds - _stationRosterBuilt < 250) return;
        _stationRosterBuilt = _stationElapsedMilliseconds;
        var roster = new List<EnbStationRosterEntry>();
        foreach (var (id, person) in _stationRemotePeople)
        {
            if (!_stationPeople.ContainsKey((false, id))) continue; // Not in this room's population.
            string name = person.Identity.FirstName;
            if (!string.IsNullOrWhiteSpace(name)) roster.Add(new("player/" + id, name, EnbStationRosterKind.Player) { Room = room.RoomType, Position = person.World.Translation });
        }
        foreach (var stationRoom in definition.Rooms)
            foreach (var npc in definition.RoomNpcs(stationRoom.RoomType))
            {
                if (string.IsNullOrWhiteSpace(npc.DisplayName)) continue;
                bool here = npc.RoomType == room.RoomType;
                Vector3? at = !here ? null : _stationPeople.TryGetValue((true, npc.Slot), out var person) ? person.World.Translation
                    : room.Resource.Hierarchies.Single().Pivots.Any(p => p.Name.Equals($"AVATAR{npc.Slot:00}", StringComparison.OrdinalIgnoreCase))
                        ? room.PivotTransform($"AVATAR{npc.Slot:00}").Translation : null;
                var kind = npc.Variant == 0 && npc.Role < 8 ? EnbStationRosterKind.Vendor : EnbStationRosterKind.Character;
                roster.Add(new($"npc/{npc.RoomType}/{npc.Slot}", npc.DisplayName, kind)
                    { Room = npc.RoomType, InRoom = here, Position = at });
            }
        // Other rooms' terminals from the definition (their places are known once that room is loaded).
        foreach (var facility in definition.Facilities)
            if (facility.RoomType != room.RoomType && definition.Rooms.Any(r => r.RoomType == facility.RoomType) && StationTerminalName(facility.Type) is { } other)
                roster.Add(new($"terminal/{facility.RoomType}/{facility.Slot}", other, EnbStationRosterKind.Terminal) { Room = facility.RoomType, InRoom = false });
        foreach (var fixture in StationFixturePlacements(room, definition))
        {
            const string prefix = "TERMINAL";
            if (!fixture.Key.StartsWith(prefix, StringComparison.Ordinal) || !fixture.Key.EndsWith("/monitor", StringComparison.Ordinal)) continue;
            uint slot = uint.Parse(fixture.Key.AsSpan(prefix.Length, 2), System.Globalization.CultureInfo.InvariantCulture);
            if (definition.Facilities.FirstOrDefault(f => f.RoomType == room.RoomType && f.Slot == slot) is { } facility && StationTerminalName(facility.Type) is { } name)
                roster.Add(new($"terminal/{room.RoomType}/{slot}", name, EnbStationRosterKind.Terminal) { Room = room.RoomType, Position = fixture.World.Translation });
        }
        Volatile.Write(ref _stationRoster, roster.ToArray());
    }

    /// <summary>
    /// The walk goal of a roster entry in the player's room, from the same picks a click uses (a vendor's booth before
    /// the vendor), or null once it is gone.
    /// </summary>
    internal EnbStationWalkGoal? StationWalkGoal(string key)
    {
        int last = key.LastIndexOf('/');
        if (last < 0 || !uint.TryParse(key.AsSpan(last + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint id)) return null;
        if (key.StartsWith("player/", StringComparison.Ordinal))
            return StationPlayer(id) is { } player ? new(player.Origin, player.InRange) : null;
        if (key.StartsWith("npc/", StringComparison.Ordinal))
        {
            var booth = Volatile.Read(ref _stationBoothPicks).FirstOrDefault(p => p.Npc.Slot == id);
            var npc = Volatile.Read(ref _stationNpcPicks).FirstOrDefault(p => p.Npc.Slot == id);
            if (booth is null && npc is null) return null;
            return new(booth?.Origin ?? npc!.Origin, booth?.InRange == true || npc?.InRange == true, booth?.Npc ?? npc!.Npc);
        }
        if (key.StartsWith("terminal/", StringComparison.Ordinal))
        {
            if (Volatile.Read(ref _stationTerminalPicks).FirstOrDefault(p => p.Facility.Slot == id) is { } terminal)
                return new(terminal.Origin, terminal.InRange, Facility: terminal.Facility);
            // A terminal with no click of its own: walk up to it.
            return StationRoster.FirstOrDefault(e => e.Key == key) is { Position: { } at } &&
                _stationMovement is { } self ? new(at, Vector3.Distance(self.Position, at) < 11) : null;
        }
        return null;
    }

    private void ClearStationRoster() { Volatile.Write(ref _stationRoster, []); _stationRosterBuilt = long.MinValue; }
}
