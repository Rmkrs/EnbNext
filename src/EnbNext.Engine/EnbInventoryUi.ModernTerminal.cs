using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI terminals: with Modern UI on, a terminal opens as a compact window that
/// can be moved (its place is kept) instead of taking the whole screen behind a camera move. The avatar still turns
/// to the terminal and takes the terminal pose others see, movement stays blocked while it is open, and every request
/// the terminal sends (open, its actions, close) is the retail one. First the Intergalactic Net: its page in the window.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    /// <summary>The open (or opening) terminal is a compact Modern UI window, chosen when it opens (kept after it closes).</summary>
    private bool _terminalCompact;
    /// <summary>The terminal opened the Modern inventory (so it closes it again).</summary>
    private bool _terminalOpenedModernInventory;
    internal bool TerminalCompact => _terminalCompact;
    private Rect _modernTerminalBounds;
    internal EnbUiPoint? ModernTerminalPosition { get; set; }
    internal Action<EnbUiPoint>? ModernTerminalMoved { get; set; }

    /// <summary>The terminals that open as a Modern UI window (Refining, Analyze, Manufacturing, Jobs and the Intergalactic Net; the customization editors stay full screen).</summary>
    private bool CompactTerminal(uint type) => ModernUi && type <= 4;

    /// <summary>The window: its frame, the terminal's name and close button (the terminal's own exit); returns the content area.</summary>
    private Rect ModernTerminalFrame(double contentWidth, double contentHeight)
    {
        double u = ModernUnit, pad = 10 * u, head = 24 * u, gap = 6 * u, title = 16 * u;
        double pageWidth = Math.Max(320 * u, Math.Min(Math.Round(contentWidth * u), _size.Width - 40 * u - pad * 2));
        double pageHeight = Math.Max(200 * u, Math.Min(Math.Round(contentHeight * u), _size.Height - 60 * u - pad * 2 - head - gap));
        double width = pad * 2 + pageWidth, height = pad * 2 + head + gap + pageHeight;
        double x, y;
        if (ModernTerminalPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        else { x = (_size.Width - width) / 2; y = (_size.Height - height) * .4; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernTerminalBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernTerminalBounds, "mterm/frame");
        _tips.Remove("mterm/frame");
        Flush();
        double cx = x + pad, cy = y + pad;
        ModernText("mterm/title", EnbWorldRenderer.StationTerminalName(_terminal?.Type ?? 4) ?? "Terminal", new(cx, cy, pageWidth - head, head), new(1, .85f, .3f), title);
        bool canClose = !(ManufacturingExitBlocked || AnalysisExitBlocked) && !JobsRequestPending;
        ModernCloseButton("terminal/exit", new(x + width - pad - head, cy, head, head), canClose ? 1 : .45f);
        if (canClose) _terminalActions["terminal/exit"] = (EnbTerminalCommand.Close, 0);
        return new(cx, cy + head + gap, pageWidth, pageHeight);
    }

    private void ModernTerminalWindow()
    {
        if (_terminal?.Type == 3) { ModernJobsWindow(); return; }
        if (_terminal?.Type == 0) { ModernRefineryWindow(); return; }
        if (_terminal?.Type == 2) { ModernManufacturingWindow(); return; }
        if (_terminal?.Type == 1) { ModernAnalysisWindow(); return; }
        double u = ModernUnit, head = 24 * u, text = 14 * u;
        var page = ModernTerminalFrame(860, 600);
        _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 120), page));
        if (_stationBrowser is { } browser) browser.Bounds = page;
        if (_message is not null)
            ModernText("mterm/message", _message, new(page.X + 12 * u, page.Y + 12 * u, page.Width - 24 * u, head), new(1, .45f, .45f), text);
        else if (_stationBrowser is not { IsVisible: true })
            ModernText("mterm/loading", "Connecting...", new(page.X, page.Y + page.Height / 2 - head / 2, page.Width, head), new(.75f, .8f, .92f), text, 1);
    }

    /// <summary>
    /// Jobs in a Modern UI window: the three kinds as tabs, the list (click a heading to sort, as the retail columns;
    /// the wheel scrolls it), and the chosen job's description with its Accept button. Every request is the retail one
    /// (the job description on a click, the accept on Accept).
    /// </summary>
    private void ModernJobsWindow()
    {
        double u = ModernUnit, text = 14 * u, small = 12 * u, head = 24 * u, gap = 8 * u, rowHeight = Math.Round(22 * u);
        var page = ModernTerminalFrame(900, 540);
        var all = JobRows();
        var rows = ModernJobRows(all);
        // The selection is a row, as in retail (the server can list the same job id twice, 005A2730 selects one row):
        // when the list or the rows shown change, the last row carrying the chosen id is selected again (005A2C50).
        if (!rows.AsSpan().SequenceEqual(_modernJobsShown) || !all.AsSpan().SequenceEqual(_modernJobsAll))
        {
            _modernJobsShown = rows; _modernJobsAll = all;
            _jobSelectedRow = _jobSelected is uint chosen ? Array.FindLastIndex(rows, j => j.Field0 == chosen) : -1;
        }
        bool locked = JobsRequestPending || _busy;
        // The kinds of job.
        string[] kinds = ["Combat", "Trade", "Explore"];
        double tabWidth = Math.Round(110 * u);
        for (int i = 0; i < kinds.Length; i++)
        {
            var tab = new Rect(page.X + i * (tabWidth + 4 * u), page.Y, tabWidth, head);
            ModernButton("jobType/" + i, kinds[i], tab, text, kinds[i] + " jobs", enabled: !locked);
            if (i == _jobType) _images.Add(new(EnbModernIcons.Fill(new(1, .85f, .3f), 200), new(tab.X + 4 * u, tab.Bottom - 2 * u, tab.Width - 8 * u, 2 * u)));
        }
        // Search and filters (Next: retail has neither).
        var (levelRect, sponsorRect) = ModernJobFilters(page, page.X + kinds.Length * (tabWidth + 4 * u) + gap, head, text);
        double top = page.Y + head + gap;
        double listWidth = Math.Round(page.Width * .58), detailX = page.X + listWidth + gap, detailWidth = page.Width - listWidth - gap;
        // The list: headings, then rows.
        var list = new Rect(page.X, top, listWidth, page.Bottom - top);
        ModernFrame("DIALOG_BOX_INFO", list);
        Flush();
        double rowsTop = list.Y + 4 * u + head + 2 * u;
        int visible = Math.Max(1, (int)((list.Bottom - 6 * u - rowsTop) / rowHeight));
        double room = rows.Length > visible ? ModernScrollbarRoom : 0;
        double[] widths = [.42, .10, .23, .25];
        string[] headings = ["Name", "Level", "Sponsor", "Reward"];
        double colX = list.X + 8 * u, inner = list.Width - 16 * u;
        for (int i = 0; i < headings.Length; i++)
        {
            double w = (inner - room) * widths[i];
            var cell = new Rect(colX, list.Y + 4 * u, w, head);
            string id = "jobSort/" + i;
            Hit(id, FromPixels(cell));
            bool sorted = _jobSortColumn == i;
            ModernText(id + "/text", headings[i], cell,
                _hover == id ? new(1, 1, .45f) : sorted ? new(1, .85f, .3f) : new(.7f, .75f, .85f), small);
            _tips[id] = sorted ? (_jobSortDescending ? "Sorted high to low. Click to reverse." : "Sorted low to high. Click to reverse.") : "Sort by " + headings[i].ToLowerInvariant();
            colX += w;
        }
        _terminalScrollMaximum = Math.Max(0, rows.Length - visible);
        SetTerminalScroll(_terminalPage);
        if (rows.Length == 0)
            ModernText("mterm/jobsNone", (_scene?.StationJobs.IsDefaultOrEmpty ?? true) ? "Waiting for the job list..." : all.Length > 0 ? "No jobs match the search and filters." : "No jobs of this kind here.",
                new(list.X + 8 * u, rowsTop, inner, rowHeight), new(.75f, .8f, .92f), text);
        for (int n = 0; n < visible && _terminalPage + n < rows.Length; n++)
        {
            int index = _terminalPage + n;
            var job = rows[index];
            var row = new Rect(list.X + 4 * u, rowsTop + n * rowHeight, list.Width - 8 * u - room, rowHeight);
            // The row number counts from the list's top (as the retail rows): 005A2730 selects that row.
            string id = $"terminal/mjob{n + 1:00}";
            if (!locked) { Hit(id, FromPixels(row)); _terminalActions[id] = (EnbTerminalCommand.JobDescription, job.Field0); }
            bool selected = _jobSelectedRow == index;
            // A job whose details were shown since the terminal opened is dimmed.
            if (selected && !_jobsSeen.Contains(job.Field0)) _jobsSeen.Add(job.Field0);
            bool seen = !selected && _jobsSeen.Contains(job.Field0);
            if (selected) { _jobReward = job.Text2; _images.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 60), row)); }
            else if (n % 2 == 1) _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 56), row));
            if (_hover == id) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(row.Width * _scale), (int)Math.Round(row.Height * _scale)), row));
            string[] cells = [job.Text0, job.Field3.ToString(System.Globalization.CultureInfo.InvariantCulture), job.Text1, job.Text2];
            double x = list.X + 8 * u;
            for (int c = 0; c < cells.Length; c++)
            {
                double w = (inner - room) * widths[c];
                ModernText($"{id}/{c}", ModernEllipsis(cells[c], w - 8 * u, text), new(x, row.Y, w - 8 * u, row.Height), selected ? new(1, 1, .45f) : seen ? new(.52f, .56f, .66f) : new(.92f, .94f, 1), text);
                x += w;
            }
            _tips[id] = job.Text0 + (seen ? "\nYou have seen this job's details" : "\nClick for the job's details");
        }
        ModernScrollbar("terminal/mjobs", new(list.X, rowsTop, list.Width, visible * rowHeight), rows.Length, visible, _terminalPage, v => SetTerminalScroll(v));
        // The detail: title, description, reward and Accept.
        var detail = JobDetail(all);
        var box = new Rect(detailX, top, detailWidth, page.Bottom - top);
        ModernFrame("DIALOG_BOX_INFO", box);
        Flush();
        double dx = box.X + 10 * u, dw = box.Width - 20 * u, dy = box.Y + 8 * u;
        if (!string.IsNullOrWhiteSpace(detail.Title))
        {
            foreach (string line in ModernWrap(detail.Title, dw, 16 * u)) { ModernText("mterm/jobTitle" + dy, line, new(dx, dy, dw, head), new(1, .85f, .3f), 16 * u); dy += head; }
            dy += 4 * u;
        }
        double buttonHeight = Math.Round(30 * u), rewardTop = box.Bottom - 10 * u - buttonHeight - gap - (string.IsNullOrWhiteSpace(detail.Reward) ? 0 : rowHeight * 2);
        // The sectors the details name, each with Plan route, above the reward.
        var places = JobPlaces(detail.Body);
        double placeHeight = JobPlaceHeight(rowHeight), placesTop = rewardTop - places.Count * placeHeight;
        for (int i = 0; i < places.Count; i++) ModernJobPlace(i, places[i], dx, placesTop + i * placeHeight, dw, rowHeight, text, small);
        foreach (string line in ModernWrap(detail.Body, dw, text))
        {
            if (dy + rowHeight > placesTop) break;
            ModernText("mterm/jobLine" + dy, line, new(dx, dy, dw, rowHeight), new(.88f, .9f, .96f), text);
            dy += rowHeight;
        }
        if (!string.IsNullOrWhiteSpace(detail.Reward))
        {
            ModernText("mterm/jobRewardTitle", "Reward", new(dx, rewardTop, dw, rowHeight), new(.65f, .7f, .85f), small);
            ModernText("mterm/jobReward", ModernEllipsis(detail.Reward, dw, text), new(dx, rewardTop + rowHeight, dw, rowHeight), new(1, .85f, .45f), text);
        }
        if (detail.AcceptJob is uint acceptJob)
        {
            bool enabled = detail.CanAccept && !locked;
            var accept = new Rect(box.Right - 10 * u - Math.Round(120 * u), box.Bottom - 10 * u - buttonHeight, Math.Round(120 * u), buttonHeight);
            ModernButton("terminal/maccept", "Accept", accept, text, "Accept this job", enabled);
            if (enabled) _terminalActions["terminal/maccept"] = (EnbTerminalCommand.JobAccept, acceptJob);
        }
        Flush();
        ModernJobFilterList(levelRect, sponsorRect, text);
    }
}
