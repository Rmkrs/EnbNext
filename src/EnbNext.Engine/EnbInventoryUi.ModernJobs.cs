using System.Globalization;
using EnbNext.Net7.Presentation;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI Jobs window extras (retail has none of them): a search box that matches a
/// job's name or sponsor, and Level and Sponsor filters (several of each at once). They only choose which of the listed
/// jobs are shown; nothing is sent to the server for them.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private EnbUiTextInput? _jobSearch;
    private readonly List<string> _jobLevels = [], _jobSponsors = [];
    /// <summary>The open filter list: 0 none, 1 levels, 2 sponsors; its choices (the first is "All") and scroll.</summary>
    private int _jobFilterOpen, _jobFilterScroll;
    private string[] _jobFilterChoices = [];
    /// <summary>The rows the window showed last (filtered); the selected row is an index into them.</summary>
    private Net7JobListEntry[] _modernJobsShown = [], _modernJobsAll = [];
    /// <summary>The jobs whose details were shown since the terminal opened (they are dimmed in the list).</summary>
    private readonly List<uint> _jobsSeen = [];
    internal IReadOnlyList<uint> ModernJobsSeen => _jobsSeen;

    public bool ModernJobSearchEditing => _jobSearch is { IsVisible: true, IsFocused: true };
    internal EnbUiTextInput? ModernJobSearchInput => _jobSearch;
    internal int ModernJobSelectedRow => _jobSelectedRow;

    /// <summary>The jobs with each job id once (its first row), without the jobs the server has withdrawn (the second
    /// copy of a job listed twice outlives the server's delete); the same array when nothing is left out.</summary>
    internal static Net7JobListEntry[] DistinctJobs(Net7JobListEntry[] rows, System.Collections.Immutable.ImmutableArray<uint> withdrawn)
    {
        List<Net7JobListEntry>? kept = null;
        for (int i = 0; i < rows.Length; i++)
        {
            uint id = rows[i].Field0;
            bool leave = !withdrawn.IsDefaultOrEmpty && withdrawn.Contains(id);
            for (int j = 0; j < i && !leave; j++) leave = rows[j].Field0 == id;
            if (leave) kept ??= new List<Net7JobListEntry>(rows.AsSpan(0, i).ToArray());
            else kept?.Add(rows[i]);
        }
        return kept?.ToArray() ?? rows;
    }

    private static string JobLevel(Net7JobListEntry job) => job.Field3.ToString(CultureInfo.InvariantCulture);

    /// <summary>The listed jobs that match the search and filters, in the list's order; a job the server lists more
    /// than once (the same job id) is shown once (it can be accepted only once).</summary>
    private Net7JobListEntry[] ModernJobRows(Net7JobListEntry[] rows)
    {
        rows = DistinctJobs(rows, _scene?.StationJobsWithdrawn ?? []);
        string query = (_jobSearch?.Text ?? "").Trim();
        if (query.Length == 0 && _jobLevels.Count == 0 && _jobSponsors.Count == 0) return rows;
        return Array.FindAll(rows, j =>
            (query.Length == 0 || (j.Text0 ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) || (j.Text1 ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))
            && (_jobLevels.Count == 0 || _jobLevels.Contains(JobLevel(j)))
            && (_jobSponsors.Count == 0 || _jobSponsors.Contains(j.Text1 ?? "")));
    }

    private static string JobFilterLabel(List<string> chosen, string all, string one, string many)
        => chosen.Count == 0 ? all : chosen.Count == 1 ? one + chosen[0] : chosen.Count.ToString(CultureInfo.InvariantCulture) + " " + many;

    /// <summary>The search box and the two filter buttons, from <paramref name="left"/> to the window's right edge.</summary>
    private (Rect Level, Rect Sponsor) ModernJobFilters(Rect page, double left, double head, double text)
    {
        double u = ModernUnit, gap = 6 * u;
        double sponsorWidth = Math.Round(190 * u), levelWidth = Math.Round(120 * u);
        var sponsor = new Rect(page.Right - sponsorWidth, page.Y, sponsorWidth, head);
        var level = new Rect(sponsor.X - gap - levelWidth, page.Y, levelWidth, head);
        var search = new Rect(left, page.Y, Math.Max(80 * u, level.X - gap - left), head);
        ModernFrame("DIALOG_BOX_INFO", search, "mjobs/search");
        Flush();
        bool editing = ModernJobSearchEditing;
        string value = _jobSearch?.Text ?? "";
        var inner = new Rect(search.X + 8 * u, search.Y, search.Width - 16 * u, head);
        if (value.Length == 0 && !editing) ModernText("mjobs/searchText", "Search", inner, new(.55f, .6f, .7f), text);
        else ModernText("mjobs/searchText", ModernEllipsis(editing ? value.Insert(Math.Clamp(_jobSearch!.CaretIndex, 0, value.Length), "|") : value, inner.Width, text), inner, new(1, 1, 1), text);
        if (_jobSearch is not null) _jobSearch.Bounds = search;
        _tips["mjobs/search"] = "Find jobs by name or sponsor";
        ModernButton("mjobs/level", JobFilterLabel(_jobLevels, "All levels", "Level ", "levels"), level, text, "Show only jobs of these levels (several at once)");
        ModernButton("mjobs/sponsor", ModernEllipsis(JobFilterLabel(_jobSponsors, "All sponsors", "", "sponsors"), sponsor.Width - 16 * u, text), sponsor, text, "Show only jobs from these sponsors (several at once)");
        return (level, sponsor);
    }

    /// <summary>The open filter's list under its button: All, then each level or sponsor of this kind of job, ticked on and off.</summary>
    private void ModernJobFilterList(Rect levelRect, Rect sponsorRect, double text)
    {
        if (_jobFilterOpen == 0) return;
        bool levels = _jobFilterOpen == 1;
        var chosen = levels ? _jobLevels : _jobSponsors;
        var kind = (_scene?.StationJobs ?? []).Where(j => j.Field1 == _jobType);
        var values = levels
            ? kind.Select(JobLevel).Concat(chosen).Distinct().OrderBy(v => v.Length).ThenBy(v => v, StringComparer.Ordinal)
            : kind.Select(j => j.Text1 ?? "").Where(v => v.Length > 0).Concat(chosen).Distinct().OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase);
        _jobFilterChoices = [levels ? "All levels" : "All sponsors", .. values];
        double u = ModernUnit, rowH = Math.Round(22 * u), gap = 4 * u;
        var button = levels ? levelRect : sponsorRect;
        int visible = Math.Min(_jobFilterChoices.Length, Math.Max(1, (int)((_size.Height - button.Bottom - gap * 4) / rowH)));
        visible = Math.Min(visible, 14);
        _jobFilterScroll = Math.Clamp(_jobFilterScroll, 0, Math.Max(0, _jobFilterChoices.Length - visible));
        double widest = _jobFilterChoices.Max(v => ModernMeasure(v, text));
        double dropWidth = Math.Min(Math.Max(button.Width, widest + 48 * u + ModernScrollbarRoom), 360 * u);
        var drop = new Rect(Snap(button.Right - dropWidth), Snap(button.Bottom + gap), dropWidth, visible * rowH + gap * 2);
        ModernFrame("DIALOG_BOX_WINDOW", drop, "mjobs/dropFrame");
        double room = _jobFilterChoices.Length > visible ? ModernScrollbarRoom : 0;
        for (int n = 0; n < visible; n++)
        {
            int i = _jobFilterScroll + n;
            string id = "mjobs/pick/" + i.ToString(CultureInfo.InvariantCulture);
            var r = new Rect(drop.X + 2 * u, drop.Y + gap + n * rowH, drop.Width - 4 * u - room, rowH);
            Hit(id, FromPixels(r));
            if (_hover == id) ModernFrame("DIALOG_BOX_OPTION", r);
            bool on = i == 0 ? chosen.Count == 0 : chosen.Contains(_jobFilterChoices[i]);
            int box = (int)Math.Round(14 * u * _scale);
            _images.Add(new(EnbModernIcons.Check(box, on, _hover == id, round: i == 0), new(Snap(r.X + 6 * u), Snap(r.Y + (r.Height - box / _scale) / 2), box / _scale, box / _scale)));
            double tx = r.X + 12 * u + box / _scale;
            ModernText(id + "/text", ModernEllipsis(_jobFilterChoices[i], r.Right - tx - 4 * u, text), new(tx, r.Y, r.Right - tx, r.Height), _hover == id || on ? new(1, 1, .3f) : new(1, 1, 1), text);
        }
        ModernScrollbar("mjobs/drop", new(drop.X, drop.Y + gap, drop.Width, visible * rowH), _jobFilterChoices.Length, visible, _jobFilterScroll, v => _jobFilterScroll = v);
        Flush();
    }

    private void ResetJobScroll() { _terminalScroll = 0; _terminalPage = 0; }

    /// <summary>Clicks on the search box, the filter buttons and their lists; any other click closes an open list.</summary>
    private bool ActivateModernJobs(string id)
    {
        if (!_terminalCompact || _terminal?.Type != 3) return false;
        if (id != "mjobs/search" && _jobSearch is { IsFocused: true }) _jobSearch.IsVisible = false;
        if (!id.StartsWith("mjobs/", StringComparison.Ordinal)) { _jobFilterOpen = 0; return false; }
        if (ActivateJobRoute(id)) return true;
        if (id == "mjobs/search")
        {
            _jobSearch ??= CreateJobSearch();
            _jobSearch.IsVisible = true; _jobSearch.Focus(); _jobFilterOpen = 0;
        }
        else if (id is "mjobs/level" or "mjobs/sponsor")
        {
            int which = id == "mjobs/level" ? 1 : 2;
            _jobFilterOpen = _jobFilterOpen == which ? 0 : which; _jobFilterScroll = 0;
            InteractionSoundRequested?.Invoke("TextClick03.wav");
        }
        else if (id.StartsWith("mjobs/pick/", StringComparison.Ordinal) && _jobFilterOpen != 0
            && int.TryParse(id.AsSpan(11), NumberStyles.None, CultureInfo.InvariantCulture, out int pick) && pick < _jobFilterChoices.Length)
        {
            // All shows everything and closes the list; a level or sponsor is ticked on or off and the list stays open.
            var chosen = _jobFilterOpen == 1 ? _jobLevels : _jobSponsors;
            if (pick == 0) { chosen.Clear(); _jobFilterOpen = 0; }
            else if (!chosen.Remove(_jobFilterChoices[pick])) chosen.Add(_jobFilterChoices[pick]);
            ResetJobScroll(); InteractionSoundRequested?.Invoke("TextClick03.wav");
        }
        return true;
    }

    /// <summary>The wheel over an open filter list scrolls it.</summary>
    internal bool ScrollModernJobs(string id, double delta)
    {
        if (_jobFilterOpen == 0 || !id.StartsWith("mjobs/", StringComparison.Ordinal) || delta == 0) return false;
        if (id.StartsWith("mjobs/pick/", StringComparison.Ordinal) || id.StartsWith("mjobs/drop", StringComparison.Ordinal))
            _jobFilterScroll = Math.Max(0, _jobFilterScroll + (delta < 0 ? 1 : -1));
        Publish(); return true;
    }

    private EnbUiTextInput CreateJobSearch()
    {
        var input = CreateTextInput(new(32), key =>
        {
            if (key is not (EnbInventoryKeys.Enter or EnbInventoryKeys.Escape or EnbInventoryKeys.Tab)) return Task.FromResult(false);
            // Escape clears the text; Enter and Tab keep it. Either way the keyboard returns to the game.
            if (key == EnbInventoryKeys.Escape && _jobSearch is { } search) search.Text = "";
            if (_jobSearch is not null) _jobSearch.IsVisible = false;
            Publish(); return Task.FromResult(true);
        });
        input.Changed += (_, _) => { ResetJobScroll(); Publish(); };
        input.LostFocus += (_, _) => Publish();
        return input;
    }
}
