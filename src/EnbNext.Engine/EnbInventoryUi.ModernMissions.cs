using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI missions window: every mission on one line, all at once: its number, name, step (n/m) and the
/// current step's text, shortened with an ellipsis. A click opens the mission beside the window with its full text
/// (no retail reveal animation) and the Track and Forfeit (or Clear) buttons. Forfeit asks once, in the pane, and
/// then sends the retail forfeit; Clear and Track behave as in the retail panel.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private bool _modernMissions;
    private Rect _modernMissionsBounds;
    /// <summary>The mission open beside the window (its id; -1: none), whether its forfeit awaits Yes/No, and the details scroll.</summary>
    private int _modernMissionSelected = -1, _modernMissionScroll;
    private bool _modernMissionConfirm;
    internal EnbUiPoint? ModernMissionsPosition { get; set; }
    internal Action<EnbUiPoint>? ModernMissionsMoved { get; set; }
    public bool ModernMissionsOpen => _modernMissions;

    public bool ToggleModernMissions()
    {
        if (!_interactive || !ModernUi) return false;
        _modernMissions = !_modernMissions;
        _modernMissionSelected = -1; _modernMissionConfirm = false; _modernMissionScroll = 0;
        if (_modernMissions) { RaiseModern("mmission"); Opened?.Invoke(); }
        InteractionSoundRequested?.Invoke("TextClick03.wav");
        Publish(); return true;
    }

    /// <summary>Opens the missions window with a mission chosen (its details shown), as a click on its row would.</summary>
    internal bool ShowModernMission(int id)
    {
        if (!_interactive || !ModernUi || Data?.Missions.Any(m => m.Id == id) != true) return false;
        if (!_modernMissions) { _modernMissions = true; Opened?.Invoke(); }
        RaiseModern("mmission");
        _modernMissionSelected = id; _modernMissionConfirm = false; _modernMissionScroll = 0;
        InteractionSoundRequested?.Invoke("TextClick03.wav");
        Publish(); return true;
    }

    private static readonly EnbRgb MissionNameColor = new(1, .45f, .38f), MissionDoneColor = new(.45f, 1, .45f);

    /// <summary>"(3/12) Do this." -> ("(3/12)", "Do this."); a step without the counter keeps all of its text.</summary>
    private static (string Counter, string Text) SplitMissionStep(string step)
    {
        step = step.Trim();
        if (step.StartsWith('(') && step.IndexOf(')') is int close and > 1 && step.AsSpan(1, close - 1).IndexOf('/') > 0)
            return (step[..(close + 1)], step[(close + 1)..].Trim());
        return ("", step);
    }

    /// <summary>The text cut to fit a width at a Modern UI size, ending in an ellipsis when it had to be shortened.</summary>
    private string ModernEllipsis(string text, double width, double size)
    {
        text = text.Replace('\n', ' ').Replace('\r', ' ');
        if (ModernMeasure(text, size) <= width) return text;
        int low = 0, high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (ModernMeasure(text[..mid].TrimEnd() + "…", size) <= width) low = mid; else high = mid - 1;
        }
        return text[..low].TrimEnd() + "…";
    }

    private void ModernMissionsWindow()
    {
        if (Data is not { } data) return;
        var missions = data.Missions;
        double u = ModernUnit, text = 14 * u, title = 16 * u, gap = 4 * u, pad = 10 * u, head = 24 * u, column = 12 * u;
        double rowHeight = Math.Round(26 * u);
        uint clientTime = _scene?.ClientTime ?? 0;
        // Columns: the number and step counter measured; the name up to a cap; the step text a fixed width.
        double numberWidth = ModernMeasure(Math.Max(1, missions.Length).ToString(CultureInfo.InvariantCulture), text);
        double nameWidth = Math.Min(Math.Round(200 * u), missions.Length == 0 ? 0 : missions.Max(m => ModernMeasure(m.Name, text)));
        double counterWidth = missions.Length == 0 ? 0 : missions.Max(m => ModernMeasure(SplitMissionStep(m.CurrentStep).Counter, text));
        double stepWidth = Math.Round(240 * u);
        bool timed = missions.Any(m => m.TimerText(clientTime).Length > 0);
        double timerWidth = timed ? ModernMeasure("00:00", text) : 0;
        double contentWidth = Math.Ceiling(8 * u + numberWidth + column + nameWidth + column + counterWidth + 6 * u + stepWidth + (timed ? column + timerWidth : 0) + 8 * u);
        contentWidth = Math.Max(contentWidth, Math.Round(360 * u));
        int rows = Math.Max(1, missions.Length);
        double width = pad * 2 + contentWidth, height = pad * 2 + head + gap + rows * rowHeight;
        double x, y;
        if (ModernMissionsPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        else { x = (_size.Width - width) / 2; y = _size.Height * .2; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernMissionsBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernMissionsBounds, "mmission/frame");
        Flush();
        double cx = x + pad, cy = y + pad;
        ModernText("mmission/title", "Missions", new(cx, cy, contentWidth - head, head), new(1, .85f, .3f), title);
        ModernCloseButton("mmission/close", new(x + width - pad - head, cy, head, head));
        cy += head + gap;
        ModernFrame("DIALOG_BOX_INFO", new(cx, cy, contentWidth, rows * rowHeight));
        Flush();
        if (missions.Length == 0) ModernText("mmission/none", "No missions.", new(cx + 8 * u, cy, contentWidth, rowHeight), new(.75f, .8f, .92f), text);
        double numberX = cx + 8 * u, nameX = numberX + numberWidth + column, counterX = nameX + nameWidth + column, stepX = counterX + counterWidth + 6 * u;
        double timerX = stepX + stepWidth + column;
        for (int i = 0; i < missions.Length; i++)
        {
            var mission = missions[i];
            var row = new Rect(cx, Snap(cy + i * rowHeight), contentWidth, rowHeight);
            string id = "mmission/row/" + mission.Id.ToString(CultureInfo.InvariantCulture);
            Hit(id, FromPixels(row));
            bool selected = mission.Id == _modernMissionSelected;
            if (i % 2 == 1) _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 56), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            if (selected) _images.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 46), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            if (selected || _hover == id) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(row.Width * _scale), (int)Math.Round(row.Height * _scale)), row));
            var (counter, stepText) = SplitMissionStep(mission.CurrentStep);
            ModernText(id + "/n", (i + 1).ToString(CultureInfo.InvariantCulture), new(numberX, row.Y, numberWidth, row.Height), new(1, .85f, .3f), text, 2);
            ModernText(id + "/name", ModernEllipsis(mission.Name, nameWidth, text), new(nameX, row.Y, nameWidth, row.Height), mission.CanClear ? MissionDoneColor : MissionNameColor, text);
            ModernText(id + "/count", counter, new(counterX, row.Y, counterWidth, row.Height), new(.75f, .8f, .92f), text);
            ModernText(id + "/step", ModernEllipsis(stepText, stepWidth, text), new(stepX, row.Y, stepWidth, row.Height), new(1, 1, 1), text);
            string timer = mission.TimerText(clientTime);
            if (timer.Length > 0) ModernText(id + "/time", timer, new(timerX, row.Y, timerWidth, row.Height), new(1, .85f, .3f), text, 2);
            _tips[id] = mission.Name + "\n\n" + string.Concat(EnbRetailDialogText.Wrap(mission.CurrentStep, 40, _ => 1).Select(line => line + "\n")) + "\nClick for details";
        }
        Flush();
        if (missions.FirstOrDefault(m => m.Id == _modernMissionSelected) is { } chosen) ModernMissionDetails(chosen);
        else { _modernMissionSelected = -1; _modernMissionConfirm = false; }
    }

    /// <summary>The selected mission beside the window: its whole text (scrolls with the wheel when long) and its buttons.</summary>
    private void ModernMissionDetails(EnbCharacterMissionSnapshot mission)
    {
        double u = ModernUnit, text = 14 * u, title = 16 * u, gap = 4 * u, pad = 12 * u, head = 24 * u, line = Math.Round(18 * u);
        var window = _modernMissionsBounds;
        double width = Math.Round(420 * u), footer = head + gap * 2;
        double textWidth = width - pad * 2;
        var lines = ModernWrap(mission.Description.Replace("\\n", "\n"), textWidth, text);
        // As tall as the text needs (at least the window), up to 22 lines and 60% of the screen; a longer text scrolls.
        double wanted = pad * 2 + head + gap + Math.Min(22, lines.Count) * line + footer;
        double height = Math.Min(Math.Max(window.Height, wanted), Math.Max(window.Height, Math.Round(_size.Height * .6)));
        double x = window.Right + gap <= _size.Width - width ? window.Right + gap : Math.Max(0, window.X - gap - width);
        double y = Math.Clamp(window.Y, 0, Math.Max(0, _size.Height - height));
        var bounds = new Rect(Snap(x), Snap(y), width, height);
        ModernFrame("DIALOG_BOX_WINDOW", bounds, "mmission/detail");
        Flush();
        double cx = bounds.X + pad, cy = bounds.Y + pad;
        ModernText("mmission/detail/title", ModernEllipsis(mission.Name, textWidth - head - gap, title), new(cx, cy, textWidth - head, head), mission.CanClear ? MissionDoneColor : MissionNameColor, title);
        ModernCloseButton("mmission/detail/close", new(bounds.Right - pad - head, cy, head, head));
        cy += head + gap;
        double textBottom = bounds.Bottom - pad - footer;
        int visible = Math.Max(1, (int)((textBottom - cy) / line));
        // A longer text keeps clear of its scroll bar.
        if (lines.Count > visible) { textWidth -= ModernScrollbarRoom; lines = ModernWrap(mission.Description.Replace("\\n", "\n"), textWidth, text); }
        _modernMissionScroll = Math.Clamp(_modernMissionScroll, 0, Math.Max(0, lines.Count - visible));
        Hit("mmission/detail/text", FromPixels(new(cx, cy, textWidth, textBottom - cy)));
        for (int i = 0; i < visible && _modernMissionScroll + i < lines.Count; i++)
        {
            string wrapped = lines[_modernMissionScroll + i];
            // "Mission Steps:" reads as a heading, as in the retail detail text.
            var color = wrapped == "Mission Steps:" ? new EnbRgb(1, .85f, .3f) : new EnbRgb(1, 1, 1);
            ModernText("mmission/detail/t" + i, wrapped, new(cx, cy + i * line, textWidth, line), color, text);
        }
        ModernScrollbar("mmission/detail/text", new(cx, cy, textWidth + ModernScrollbarRoom, visible * line), lines.Count, visible, _modernMissionScroll, v => _modernMissionScroll = v);
        // Buttons along the bottom: Track on the left, Forfeit/Clear on the right; Forfeit asks once before it is sent.
        double by = bounds.Bottom - pad - head, buttonWidth = Math.Ceiling(Math.Max(ModernMeasure("Untrack", text), ModernMeasure("Forfeit", text)) + 40 * u);
        if (_modernMissionConfirm && mission.CanForfeit)
        {
            ModernText("mmission/detail/ask", "Forfeit this mission?", new(cx, by, textWidth, head), new(1, .55f, .3f), text);
            ModernButton("mmission/forfeit/no", "No", new(bounds.Right - pad - buttonWidth, by, buttonWidth, head), text, "Keep the mission.");
            ModernButton("mmission/forfeit/yes", "Yes", new(bounds.Right - pad - buttonWidth * 2 - gap * 2, by, buttonWidth, head), text, "Forfeit " + mission.Name + ".");
        }
        else
        {
            _modernMissionConfirm = false;
            double wikiX = cx;
            if (MissionTrackingEnabled && !mission.CanClear)
            {
                bool tracked = IsMissionTracked?.Invoke(mission.Id) == true;
                ModernButton("mmission/track", tracked ? "Untrack" : "Track", new(cx, by, buttonWidth, head), text,
                    tracked ? "Stop showing this mission in the mission tracker." : "Show this mission in the mission tracker.");
                wikiX += buttonWidth + gap * 2;
            }
            // Next: the mission's page on the Net-7 Wiki, in the in-game wiki window.
            if (BrowserFactory is not null)
                ModernButton("mmission/wiki", "Wiki", new(wikiX, by, buttonWidth, head), text, "Look this mission up on the Net-7 Wiki.");
            if (mission.CanForfeit || mission.CanClear)
                ModernButton("mmission/forfeit", mission.CanClear ? "Clear" : "Forfeit", new(bounds.Right - pad - buttonWidth, by, buttonWidth, head), text,
                    mission.CanClear ? "Remove this finished mission from the list." : "Give up this mission.");
        }
        Flush();
    }

    private async Task<bool> ActivateModernMissions(string id, bool right)
    {
        if (!id.StartsWith("mmission/", StringComparison.Ordinal)) return false;
        if (right) return true;
        var mission = Data?.Missions.FirstOrDefault(m => m.Id == _modernMissionSelected);
        switch (id)
        {
            case "mmission/close": _modernMissions = false; _modernMissionSelected = -1; _modernMissionConfirm = false; InteractionSoundRequested?.Invoke("TextClick03.wav"); return true;
            case "mmission/detail/close": _modernMissionSelected = -1; _modernMissionConfirm = false; InteractionSoundRequested?.Invoke("Roll_Select02b.wav"); return true;
            case "mmission/wiki":
                if (mission is not null) OpenModernWiki(mission.Name);
                return true;
            case "mmission/track":
                if (mission is not null && MissionTrackingEnabled) { InteractionSoundRequested?.Invoke("TextClick03.wav"); MissionTrackToggled?.Invoke(mission.Id); }
                return true;
            case "mmission/forfeit":
                if (_busy || _owner is not uint owner || mission is null) return true;
                InteractionSoundRequested?.Invoke("TextClick03.wav");
                if (mission.CanClear && ClearMissionRequested is { } clear)
                {
                    var clearRequest = new EnbCharacterActionRequest(owner, _ship, mission.Slot, mission.Id);
                    await Send(async () =>
                    {
                        bool sent = await clear(clearRequest);
                        if (sent && clearRequest.Character == session() && clearRequest.ShipOwner == _ship) _modernMissionSelected = -1;
                        return sent;
                    });
                }
                else if (mission.CanForfeit) _modernMissionConfirm = true;
                return true;
            case "mmission/forfeit/no": _modernMissionConfirm = false; InteractionSoundRequested?.Invoke("Roll_Select02b.wav"); return true;
            case "mmission/forfeit/yes":
                // The retail forfeit (005C66D0 queues 0086 and plays Mission_Forfeited), for the mission open in the pane.
                if (_busy || _owner is not uint forfeitOwner || mission is not { CanForfeit: true } || ForfeitRequested is not { } forfeit) return true;
                _modernMissionConfirm = false;
                var request = new EnbCharacterActionRequest(forfeitOwner, _ship, mission.Slot, mission.Id);
                await Send(async () =>
                {
                    bool sent = await forfeit(request);
                    if (sent && request.Character == session() && request.ShipOwner == _ship) { InteractionSoundRequested?.Invoke("Mission_Forfeited"); _modernMissionSelected = -1; }
                    return sent;
                });
                return true;
        }
        if (id.StartsWith("mmission/row/", StringComparison.Ordinal) && int.TryParse(id.AsSpan(13), NumberStyles.Integer, CultureInfo.InvariantCulture, out int clicked))
        {
            _modernMissionSelected = _modernMissionSelected == clicked ? -1 : clicked;
            _modernMissionConfirm = false; _modernMissionScroll = 0;
            InteractionSoundRequested?.Invoke("fontclick.wav");
        }
        return true;
    }

    /// <summary>The wheel over the mission details scrolls its text.</summary>
    internal bool ScrollModernMission(string id, double delta)
    {
        if (!_modernMissions || _modernMissionSelected < 0 || !id.StartsWith("mmission/detail", StringComparison.Ordinal) || delta == 0) return false;
        _modernMissionScroll = Math.Max(0, _modernMissionScroll + (delta < 0 ? 3 : -3));
        Publish(); return true;
    }
}
