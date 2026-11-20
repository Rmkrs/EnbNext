using EnbNext.Net7.Protocol;
using EnbNext.Presentation;

namespace EnbNext.Engine;

/// <summary>Local /way chat command: sets or clears the waypoint arrow. Never sent to the server.</summary>
public static class EnbWaypointCommand
{
    public static bool Matches(string text)
    {
        var t = text.TrimStart();
        return t.Equals("/way", StringComparison.OrdinalIgnoreCase) || t.StartsWith("/way ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Runs the command and returns the line to show in chat.</summary>
    public static string Execute(string text, EnbWaypointBook book, uint? currentSector, Net7GalaxyMapData? galaxy)
    {
        string arguments = text.TrimStart()[4..];
        if (arguments.Trim().Length == 0)
            return book.Active is { } active ? $"Waypoint: {Describe(active)}. {EnbWayCommand.Usage}" : EnbWayCommand.Usage;
        var command = EnbWayCommand.Parse(arguments);
        if (command.Error is not null) return command.Error;
        if (command.Clear) { book.SetActive(null); return "Waypoint cleared."; }
        uint sector; string sectorName;
        if (command.Sector is null)
        {
            if (currentSector is not uint current) return "No current sector; give a sector name.";
            sector = current;
            sectorName = galaxy?.Sectors.FirstOrDefault(s => s.SectorHashId == current)?.Name ?? "this sector";
        }
        else
        {
            var sectors = galaxy?.Sectors ?? [];
            var match = sectors.Where(s => s.Name.Equals(command.Sector, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (match.Length == 0) match = sectors.Where(s => s.Name.StartsWith(command.Sector, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (match.Length == 0) match = sectors.Where(s => s.Name.Contains(command.Sector, StringComparison.OrdinalIgnoreCase)).ToArray();
            match = match.DistinctBy(s => s.SectorHashId).ToArray();
            if (match.Length == 0) return $"Unknown sector \"{command.Sector}\".";
            if (match.Length > 1)
                return $"\"{command.Sector}\" matches {string.Join(", ", match.Take(5).Select(s => s.Name))}{(match.Length > 5 ? ", ..." : "")}.";
            sector = match[0].SectorHashId; sectorName = match[0].Name;
        }
        var waypoint = new EnbWaypoint(sector, sectorName, command.Kilometres * EnbWayCommand.WorldUnitsPerKilometre, command.Label);
        book.SetActive(waypoint);
        return $"Waypoint set: {Describe(waypoint)}.";
    }

    public static string Describe(EnbWaypoint waypoint) =>
        (waypoint.Label.Length > 0 ? waypoint.Label + ", " : "") + waypoint.SectorName + " " + EnbWayCommand.Format(waypoint.Position);
}
