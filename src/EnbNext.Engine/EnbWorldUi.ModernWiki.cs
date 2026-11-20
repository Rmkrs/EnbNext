using System.Text.RegularExpressions;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>
/// Next routes to places a text names: the wiki window's route buttons and the job
/// terminal's destination. Only what the game itself knows is used: the sectors (and their systems and gates) of the
/// installed GalaxyMap.dat. A route is planned as a click on that sector in the galaxy map plans it.
/// </summary>
internal sealed partial class EnbWorldUi
{
    /// <summary>A name as the game shows it (GalaxyMap.dat spells some apostrophes as UTF-8 read as Latin-1).</summary>
    private static string PlaceName(string name) => name.Replace("\u00e2\u0080\u0099", "'").Replace("\u2019", "'");

    private IEnumerable<(string Name, uint Sector)> WikiPlaces()
    {
        if (Map.GalaxyMapAccessor?.Invoke() is not { } galaxy) yield break;
        foreach (var sector in EnbGalaxyMapPresentation.VisibleSectors(galaxy))
            yield return (PlaceName(sector.Name), sector.SectorHashId);
    }

    private bool WikiRoute(uint sector, string name)
    {
        if (!Map.RoutePlannerEnabled || modernScene is not { } scene) return false;
        Map.Guidance.Choose(sector);
        Map.Guidance.Update(Map.GalaxyMapAccessor?.Invoke(), scene);
        return Map.Guidance.Destination == sector;
    }

    /// <summary>The sectors a text names, with their system and the jumps from here (null: no route known).</summary>
    private IReadOnlyList<EnbNamedSector> NamedSectors(string text)
    {
        if (!Map.RoutePlannerEnabled || text.Length == 0 || Map.GalaxyMapAccessor?.Invoke() is not { } galaxy) return [];
        var sectors = EnbGalaxyMapPresentation.VisibleSectors(galaxy).ToArray();
        var named = FindNamedSectors(text, sectors.Select(s => (s.SectorHashId, PlaceName(s.Name))));
        if (named.Count == 0) return [];
        uint? here = modernScene is { } scene ? EnbSectorRouteGuidance.CurrentSector(scene, galaxy, Map.Guidance.Place?.Invoke()) : null;
        var result = new List<EnbNamedSector>(named.Count);
        foreach (var (id, name) in named)
        {
            var sector = sectors.First(s => s.SectorHashId == id);
            string system = galaxy.Systems.FirstOrDefault(s => s.SystemHashId == sector.Field2)?.Name is { } called ? PlaceName(called) : "";
            int? jumps = here is uint from && EnbSectorRouteGuidance.BuildRoute(galaxy, from, id) is { Count: > 0 } route ? route.Count - 1 : null;
            result.Add(new(id, name, system, jumps));
        }
        return result;
    }

    /// <summary>The sectors a text names: whole words, any case (a name under four letters only as written); a
    /// leading "The" may be left out ("Valkyrie Twins" is "The Valkyrie Twins"); where names overlap the longest
    /// ("High Earth", not "Earth"); each once, in the order the text first names them. A name that is part of a
    /// station's name ("Mercury Station in Glory's Orbit") counts only when the text names no other sector.</summary>
    internal static IReadOnlyList<(uint Sector, string Name)> FindNamedSectors(string text, IEnumerable<(uint Sector, string Name)> sectors)
    {
        string plain = PlaceName(text);
        var found = new List<(int At, int Length, uint Sector, string Name, bool Station)>();
        foreach (var (id, raw) in sectors)
        {
            string name = PlaceName(raw).Trim();
            if (name.Length == 0) continue;
            // A short name ("Io") only as the game writes it, so a common word is not taken for it.
            var options = RegexOptions.CultureInvariant | (name.Length < 4 ? RegexOptions.None : RegexOptions.IgnoreCase);
            string pattern = name.StartsWith("The ", StringComparison.OrdinalIgnoreCase) && name.Length > 8
                ? @"(?:the\s+)?" + Words(name[4..].TrimStart()) : Words(name);
            if (pattern.StartsWith("(?:the", StringComparison.Ordinal)) options |= RegexOptions.IgnoreCase;
            foreach (Match m in Regex.Matches(plain, @"(?<![\p{L}\p{Nd}])" + pattern + @"(?![\p{L}\p{Nd}])", options, TimeSpan.FromSeconds(1)))
                found.Add((m.Index, m.Length, id, name, StationWord.IsMatch(plain, m.Index + m.Length)));
        }
        var kept = found.OrderBy(f => f.At).Where(f => !found.Any(o => o.Length > f.Length && o.At <= f.At && o.At + o.Length >= f.At + f.Length)).ToList();
        bool other = kept.Exists(f => !f.Station);
        var result = new List<(uint Sector, string Name)>();
        foreach (var f in kept)
        {
            if (other && f.Station && !kept.Exists(o => o.Sector == f.Sector && !o.Station)) continue;
            if (!result.Exists(r => r.Sector == f.Sector)) result.Add((f.Sector, f.Name));
        }
        return result;
    }

    /// <summary>A name's words, any spacing between them.</summary>
    private static string Words(string name) => string.Join(@"\s+", name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));

    /// <summary>Right after a name, a word that makes it a station's or base's name.</summary>
    private static readonly Regex StationWord = new(@"\G\s+(?:station|starbase|outpost|platform|spaceport)(?![\p{L}\p{Nd}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}

/// <summary>A sector a text names: its id, name, system and the jumps from here (null: no route known).</summary>
internal readonly record struct EnbNamedSector(uint Sector, string Name, string System, int? Jumps);
