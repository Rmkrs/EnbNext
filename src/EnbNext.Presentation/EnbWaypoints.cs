using System.Globalization;
using System.Numerics;

namespace EnbNext.Presentation;

/// <summary>A personal map marker. Position is in world units; the map and /way show kilometres.</summary>
public sealed record EnbMapNote(Guid Id, uint Sector, string SectorName, Vector3 Position, string Name, string Note = "");

/// <summary>The single target of the waypoint arrow.</summary>
public sealed record EnbWaypoint(uint Sector, string SectorName, Vector3 Position, string Label, Guid? NoteId = null);

/// <summary>Parsed /way arguments. Coordinates are kilometres as shown by the map readout.</summary>
public sealed record EnbWayCommand(bool Clear, string? Sector, Vector3 Kilometres, string Label, string? Error)
{
    public const float WorldUnitsPerKilometre = 1000;
    public const string Usage = "Usage: /way [sector,] x y [z] [name]  —  /way \"sector name\" x y [z] [name]  —  /way clear";

    /// <summary>
    /// Text after "/way". A sector is optional (current sector when left out). Names with spaces are
    /// quoted or end with a comma; an unquoted, comma-less sector name must not contain numbers.
    /// </summary>
    public static EnbWayCommand Parse(string arguments)
    {
        string text = arguments.Trim();
        if (text.Length == 0) return Fail(Usage);
        if (text.Equals("clear", StringComparison.OrdinalIgnoreCase) || text.Equals("off", StringComparison.OrdinalIgnoreCase))
            return new(true, null, default, "", null);
        string? sector = null;
        if (text[0] == '"')
        {
            int close = text.IndexOf('"', 1);
            if (close < 0) return Fail("Missing closing quote after the sector name.");
            sector = text[1..close].Trim();
            text = text[(close + 1)..].TrimStart().TrimStart(',').TrimStart();
        }
        else if (text.IndexOf(',') is int comma and > 0 && text[..comma].Any(char.IsLetter) &&
            !Number(text[..comma].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0], out _))
        {
            sector = text[..comma].Trim();
            text = text[(comma + 1)..].TrimStart();
        }
        var tokens = text.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        int index = 0;
        if (sector is null)
        {
            // Unquoted sector words before the first number.
            while (index < tokens.Length && !Number(tokens[index], out _)) index++;
            if (index > 0) sector = string.Join(' ', tokens[..index]);
        }
        if (index + 1 >= tokens.Length || !Number(tokens[index], out float x) || !Number(tokens[index + 1], out float y))
            return Fail("Give at least an x and y coordinate. " + Usage);
        index += 2; float z = 0;
        if (index < tokens.Length && Number(tokens[index], out float parsedZ)) { z = parsedZ; index++; }
        string label = string.Join(' ', tokens[index..]);
        if (sector is { Length: 0 }) sector = null;
        return new(false, sector, new(x, y, z), label, null);
    }

    private static EnbWayCommand Fail(string message) => new(false, null, default, "", message);
    private static bool Number(string token, out float value) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    public static string Format(Vector3 world) => string.Create(CultureInfo.InvariantCulture,
        $"{world.X / WorldUnitsPerKilometre:0.##} {world.Y / WorldUnitsPerKilometre:0.##} {world.Z / WorldUnitsPerKilometre:0.##}");
}
