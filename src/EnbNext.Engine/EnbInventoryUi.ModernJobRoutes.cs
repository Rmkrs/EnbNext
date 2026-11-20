using System.Globalization;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next job destinations (retail has none): under a job's details in the Modern Jobs window,
/// the galaxy map's sectors the details name, each with its system, the jumps from here and a button that plans the
/// route there as the galaxy map does. Only the game's own galaxy map is used to recognise them.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    /// <summary>The sectors a text names, with their system and jumps; supplied by the world.</summary>
    internal Func<string, IReadOnlyList<EnbNamedSector>>? NamedSectors { get; set; }
    private const int JobPlacesShown = 3;
    private string? _jobPlacesText;
    private long _jobPlacesAt;
    private IReadOnlyList<EnbNamedSector> _jobPlaces = [];
    /// <summary>The last route asked for from a job: its sector and whether it was planned.</summary>
    private (uint Sector, bool Planned)? _jobRoute;

    internal IReadOnlyList<EnbNamedSector> ModernJobPlaces => _jobPlaces;

    /// <summary>The sectors the job's details name (looked up again when the details change, or after a second so
    /// the jumps follow the ship).</summary>
    private IReadOnlyList<EnbNamedSector> JobPlaces(string body)
    {
        long now = Environment.TickCount64;
        if (_jobPlacesText != body || now - _jobPlacesAt > 1000)
        {
            if (_jobPlacesText != body) _jobRoute = null;
            _jobPlacesText = body; _jobPlacesAt = now;
            var all = string.IsNullOrWhiteSpace(body) ? [] : NamedSectors?.Invoke(body) ?? [];
            _jobPlaces = all.Count > JobPlacesShown ? all.Take(JobPlacesShown).ToArray() : all;
        }
        return _jobPlaces;
    }

    private static string JobPlaceJumps(EnbNamedSector place) => place.Jumps switch
    {
        0 => "You are here",
        1 => "1 jump from here",
        int n => n.ToString(CultureInfo.InvariantCulture) + " jumps from here",
        null => "",
    };

    /// <summary>The height of one destination: its name line, then the jumps and Plan route.</summary>
    private double JobPlaceHeight(double rowHeight) => rowHeight + Math.Round(26 * ModernUnit) + 6 * ModernUnit;

    /// <summary>One destination: "Destination" and the sector with its system; under it the jumps and Plan route.</summary>
    private void ModernJobPlace(int index, EnbNamedSector place, double x, double y, double width, double rowHeight, double text, double small)
    {
        double u = ModernUnit, button = Math.Round(120 * u), buttonHeight = Math.Round(26 * u);
        string label = index == 0 ? "Destination" : "Also named";
        double labelWidth = Math.Round(80 * u);
        ModernText($"mjobs/placeLabel/{index}", label, new(x, y, labelWidth, rowHeight), new(.65f, .7f, .85f), small);
        string where = place.System.Length > 0 ? $"{place.Name}, {place.System} system" : place.Name;
        ModernText($"mjobs/place/{index}", ModernEllipsis(where, width - labelWidth, text), new(x + labelWidth, y, width - labelWidth, rowHeight), new(.75f, .9f, 1), text);
        double below = y + rowHeight + 2 * u;
        string jumps = JobPlaceJumps(place);
        if (jumps.Length > 0) ModernText($"mjobs/placeJumps/{index}", ModernEllipsis(jumps, width - labelWidth - button - 6 * u, small), new(x + labelWidth, below, width - labelWidth - button - 6 * u, buttonHeight), new(.65f, .7f, .85f), small);
        bool planned = _jobRoute is { Planned: true } r && r.Sector == place.Sector;
        bool failed = _jobRoute is { Planned: false } f && f.Sector == place.Sector;
        string tip = planned ? $"The route to {place.Name} is planned" : failed ? $"No route to {place.Name} is known" : $"Plan the route to {place.Name}";
        ModernButton($"mjobs/route/{index}", planned ? "Route planned" : failed ? "No route" : "Plan route", new Rect(x + width - button, below, button, buttonHeight), small, tip, place.Jumps != 0);
    }

    private bool ActivateJobRoute(string id)
    {
        if (!id.StartsWith("mjobs/route/", StringComparison.Ordinal)
            || !int.TryParse(id.AsSpan(12), NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index >= _jobPlaces.Count) return false;
        var place = _jobPlaces[index];
        bool planned = WikiRouteRequested?.Invoke(place.Sector, place.Name) == true;
        _jobRoute = (place.Sector, planned);
        InteractionSoundRequested?.Invoke(planned ? "TextClick01.wav" : "fontclick.wav");
        return true;
    }
}
