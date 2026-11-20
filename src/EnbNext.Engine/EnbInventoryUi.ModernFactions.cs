using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI factions window: every faction on one line, all at once: its name and its standing as one clean
/// bar (the retail hate and love gauges between the Modern HUD's bar stops, nothing of the retail channel) with the
/// reaction value on it. The faction the character is affiliated with stands first, on its own; the others follow in
/// the retail panel's order. A click opens the faction's retail description beside the window.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private bool _modernFactions;
    private Rect _modernFactionsBounds;
    /// <summary>The faction open beside the window (its key; null: none) and the details scroll.</summary>
    private string? _modernFactionSelected, _modernFactionPreview, _modernFactionShown;
    private int _modernFactionScroll;
    internal EnbUiPoint? ModernFactionsPosition { get; set; }
    internal Action<EnbUiPoint>? ModernFactionsMoved { get; set; }
    /// <summary>The Modern HUD's bar end stops (left, right), so these bars read like its hull and shield bars.</summary>
    internal Func<(EnbUiRaster Left, EnbUiRaster Right)?>? ModernBarStops { get; set; }
    public bool ModernFactionsOpen => _modernFactions;

    public bool ToggleModernFactions()
    {
        if (!_interactive || !ModernUi) return false;
        _modernFactions = !_modernFactions;
        _modernFactionSelected = null; _modernFactionScroll = 0;
        if (_modernFactions) { RaiseModern("mfaction"); Opened?.Invoke(); }
        InteractionSoundRequested?.Invoke("TextClick03.wav");
        Publish(); return true;
    }

    /// <summary>The character's faction entry for a faction key (as a ship's data names it), if it is in the list.</summary>
    private EnbCharacterFactionSnapshot? ModernFactionEntry(string key)
    {
        if (Data is not { } data || key.Length == 0) return null;
        string name = FactionName(key);
        return data.Factions.FirstOrDefault(f => f.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? data.Factions.FirstOrDefault(f => FactionName(f.Name).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The faction's display name when the character's faction list has it (the target menu offers it only then).</summary>
    internal string? ModernFactionName(string key) => ModernFactionEntry(key) is { } entry ? FactionName(entry.Name) : null;

    /// <summary>Opens the Modern UI Factions window on a faction (its details shown), as a click on its row would.</summary>
    internal bool ShowModernFaction(string key)
    {
        if (!_interactive || !ModernUi || ModernFactionEntry(key) is not { } entry) return false;
        if (!_modernFactions) { _modernFactions = true; Opened?.Invoke(); }
        RaiseModern("mfaction");
        _modernFactionSelected = entry.Name; _modernFactionScroll = 0;
        InteractionSoundRequested?.Invoke("TextClick03.wav");
        Publish(); return true;
    }

    private static readonly EnbRgb FactionAffiliatedColor = new(.35f, 1, .35f), FactionHatedColor = new(1, .25f, .25f), FactionLovedColor = new(.35f, .55f, 1);

    private string FactionName(string key) => !string.IsNullOrWhiteSpace(key) && _assets?.Factions?.TryResolve(key, out var f) == true && f.Name.Length > 0 ? f.Name : key;

    /// <summary>The faction the character is affiliated with (the retail panel's "You are affiliated with" text), if it is in the list.</summary>
    private EnbCharacterFactionSnapshot? AffiliatedFaction(EnbSceneInventorySnapshot data)
    {
        string affiliation = data.CharacterStats.GetValueOrDefault("Affiliation", "").Trim();
        if (affiliation.Length == 0) return null;
        return data.Factions.FirstOrDefault(f => FactionName(f.Name).Equals(affiliation, StringComparison.OrdinalIgnoreCase))
            ?? data.Factions.FirstOrDefault(f => f.Name.Equals(affiliation, StringComparison.OrdinalIgnoreCase));
    }

    private static string FactionValue(float reaction) => Math.Round(reaction).ToString("#,0", CultureInfo.InvariantCulture);

    private void ModernFactionsWindow()
    {
        if (Data is not { } data) return;
        var affiliated = AffiliatedFaction(data);
        var others = data.Factions.Where(f => !ReferenceEquals(f, affiliated)).ToArray();
        double u = ModernUnit, text = 14 * u, small = 12 * u, title = 16 * u, gap = 4 * u, pad = 10 * u, head = 24 * u, column = 14 * u;
        double rowHeight = Math.Round(26 * u), labelHeight = Math.Round(18 * u), section = Math.Round(8 * u);
        double nameWidth = Math.Min(Math.Round(240 * u), data.Factions.Length == 0 ? 0 : data.Factions.Max(f => ModernMeasure(FactionName(f.Name), text)));
        double barWidth = Math.Round(260 * u);
        double contentWidth = Math.Max(Math.Ceiling(8 * u + nameWidth + column + barWidth + 8 * u), Math.Round(300 * u));
        int rows = Math.Max(1, others.Length);
        double affiliatedHeight = affiliated is null ? 0 : rowHeight + section;
        double width = pad * 2 + contentWidth, height = pad * 2 + head + labelHeight + affiliatedHeight + rows * rowHeight;
        double x, y;
        if (ModernFactionsPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        else { x = (_size.Width - width) / 2; y = (_size.Height - height) * .4; }
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernFactionsBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernFactionsBounds, "mfaction/frame");
        Flush();
        double cx = x + pad, cy = y + pad;
        ModernText("mfaction/title", "Factions", new(cx, cy, contentWidth - head, head), new(1, .85f, .3f), title);
        ModernCloseButton("mfaction/close", new(x + width - pad - head, cy, head, head));
        cy += head;
        double nameX = cx + 8 * u, barX = cx + contentWidth - 8 * u - barWidth;
        // The retail panel's Hated / Loved labels over the bar column, and Next's Neutral over the neutral zone; each says
        // how a faction treats you there.
        var labelBar = new Rect(barX, cy, barWidth, labelHeight);
        var (neutralLeft, neutralRight) = FactionNeutralSpan(labelBar);
        double neutralWidth = Math.Ceiling(ModernMeasure("Neutral", small) + 4 * u), neutralX = Snap((neutralLeft + neutralRight) / 2 - neutralWidth / 2);
        double hatedWidth = Math.Ceiling(ModernMeasure("Hated", small) + 4 * u), lovedWidth = Math.Ceiling(ModernMeasure("Loved", small) + 4 * u);
        void Label(string id, string label, Rect area, EnbRgb color, int align, string tip)
        { Hit(id, FromPixels(area)); ModernText(id + "/text", label, area, _hover == id ? new(1, 1, .6f) : color, small, align); _tips[id] = tip; }
        Label("mfaction/hated", "Hated", new(barX, cy, hatedWidth, labelHeight), FactionHatedColor, 0, "Hostile below -1,000: its ships attack you on sight.");
        Label("mfaction/neutral", "Neutral", new(neutralX, cy, neutralWidth, labelHeight), FactionNeutralColor, 1, "Neutral from -1,000 to 1,999 (the amber zone on the bars): its ships leave you alone.");
        Label("mfaction/loved", "Loved", new(barX + barWidth - lovedWidth, cy, lovedWidth, labelHeight), FactionLovedColor, 2, "Friendly from 2,000.");
        cy += labelHeight;
        if (affiliated is not null)
        {
            ModernFrame("DIALOG_BOX_INFO", new(cx, cy, contentWidth, rowHeight));
            Flush();
            FactionRow(affiliated, new(cx, Snap(cy), contentWidth, rowHeight), false, true);
            cy += rowHeight + section;
        }
        ModernFrame("DIALOG_BOX_INFO", new(cx, cy, contentWidth, rows * rowHeight));
        Flush();
        if (others.Length == 0) ModernText("mfaction/none", "No factions.", new(nameX, cy, contentWidth, rowHeight), new(.75f, .8f, .92f), text);
        for (int i = 0; i < others.Length; i++) FactionRow(others[i], new(cx, Snap(cy + i * rowHeight), contentWidth, rowHeight), i % 2 == 1, false);
        Flush();
        // The details: of the faction under the pointer (while the pointer stays on the factions or their details), else
        // of the one clicked (it stays open).
        if (_hover is { } hovered && hovered.StartsWith("mfaction/row/", StringComparison.Ordinal)) _modernFactionPreview = hovered[13..];
        else if (_hover is null || !_hover.StartsWith("mfaction/", StringComparison.Ordinal)) _modernFactionPreview = null;
        if (_modernFactionSelected is not null && !data.Factions.Any(f => f.Name == _modernFactionSelected)) _modernFactionSelected = null;
        string? shown = _modernFactionPreview ?? _modernFactionSelected;
        if (shown != _modernFactionShown) { _modernFactionShown = shown; _modernFactionScroll = 0; }
        if (data.Factions.FirstOrDefault(f => f.Name == shown) is { } chosen) ModernFactionDetails(chosen, ReferenceEquals(chosen, affiliated));

        void FactionRow(EnbCharacterFactionSnapshot faction, Rect row, bool stripe, bool isAffiliated)
        {
            string id = "mfaction/row/" + faction.Name;
            Hit(id, FromPixels(row));
            bool selected = faction.Name == _modernFactionSelected;
            if (stripe) _images.Add(new(EnbModernIcons.Fill(new(0, 0, 0), 56), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            if (selected) _images.Add(new(EnbModernIcons.Fill(new(.45f, .65f, 1), 46), new(row.X + 2 * u, row.Y, row.Width - 4 * u, row.Height)));
            if (selected || _hover == id) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(row.Width * _scale), (int)Math.Round(row.Height * _scale)), row));
            string name = FactionName(faction.Name);
            ModernText(id + "/name", ModernEllipsis(name, nameWidth, text), new(nameX, row.Y, nameWidth, row.Height), isAffiliated ? FactionAffiliatedColor : new(1, 1, 1), text);
            ModernFactionBar(id, new(barX, Snap(row.Center.Y - Math.Round(14 * u) / 2), barWidth, Math.Round(14 * u)), faction, small);
            // No row tooltip in the Modern UI: hovering the row shows its details beside the window instead.
            _tips.Remove(id);
        }
    }

    /// <summary>
    /// One faction's standing: stop, hate gauge | love gauge, stop, with the reaction value on it. The gauges are the retail
    /// panel's (TSI30A HATE_BAR / LOVE_BAR) with the retail fill (0058E740); the dark channel around them is left out.
    /// </summary>
    private void ModernFactionBar(string id, Rect bar, EnbCharacterFactionSnapshot faction, double size)
    {
        var stops = ModernBarStops?.Invoke();
        double stop = stops is null ? 0 : Math.Round(bar.Height * 6 / 13 * _scale) / _scale;
        double inner = bar.Width - 2 * stop, half = Math.Round(inner / 2 * _scale) / _scale;
        var hate = new Rect(bar.X + stop, bar.Y, half, bar.Height);
        var love = new Rect(hate.Right, bar.Y, inner - half, bar.Height);
        var (loveFill, hateFill) = faction.GaugeFractions;
        Flush();
        Draw("factions/HATE_BAR00", FromPixels(hate));
        if (_draws.Count > 0) _draws[^1] = _draws[^1] with { Stage0UvOffset = new(0, .46f * hateFill) };
        Draw("factions/LOVE_BAR00", FromPixels(love));
        if (_draws.Count > 0) _draws[^1] = _draws[^1] with { Stage0UvOffset = new(0, .46f * loveFill) };
        // Next: the neutral standing between hostile and friendly (FactionStance), over the gauges' middle.
        var (neutralLeft, neutralRight) = FactionNeutralSpan(bar);
        _images.Add(new(EnbModernIcons.Fill(FactionNeutralColor, 95), new(Snap(neutralLeft), bar.Y, Snap(neutralRight) - Snap(neutralLeft), bar.Height)));
        if (stops is { } s)
        {
            _images.Add(new(s.Left, new(hate.X - stop, bar.Y, stop, bar.Height)));
            _images.Add(new(s.Right, new(love.Right, bar.Y, stop, bar.Height)));
        }
        ModernText(id + "/value", FactionValue(faction.Reaction), bar, new(1, 1, 1), size, 1);
        Flush();
    }

    /// <summary>Where the neutral zone (-1,000 to 1,999) lies on a faction bar of this place and size.</summary>
    private (double Left, double Right) FactionNeutralSpan(Rect bar)
    {
        var stops = ModernBarStops?.Invoke();
        double height = Math.Round(14 * ModernUnit);
        double stop = stops is null ? 0 : Math.Round(height * 6 / 13 * _scale) / _scale;
        double inner = bar.Width - 2 * stop, half = Math.Round(inner / 2 * _scale) / _scale, middle = bar.X + stop + half;
        return (middle - half * (-FactionHostileBelow / 10000), middle + (inner - half) * (FactionFriendlyFrom / 10000));
    }

    /// <summary>The selected faction beside the window: its name, standing, the known reactions of kills (which kills help
    /// or harm it; what defeating its ships does to the others) and its retail description (cfaction_t.ini).</summary>
    private void ModernFactionDetails(EnbCharacterFactionSnapshot faction, bool isAffiliated)
    {
        double u = ModernUnit, text = 14 * u, title = 16 * u, gap = 4 * u, pad = 12 * u, head = 24 * u, line = Math.Round(18 * u);
        var window = _modernFactionsBounds;
        double width = Math.Round(480 * u), textWidth = width - pad * 2;
        string description = _assets?.Factions?.TryResolve(faction.Name, out var definition) == true ? definition.Description ?? "" : "";
        var lines = FactionDetailLines(faction.Name, description, textWidth, text);
        double standing = line * (isAffiliated ? 2 : 1) + gap * 2;
        // As tall as the text needs, up to 24 lines and 60% of the screen; a longer text scrolls.
        double wanted = pad * 2 + head + gap + standing + Math.Max(1, Math.Min(24, lines.Count)) * line;
        double height = Math.Min(wanted, Math.Max(head * 4, Math.Round(_size.Height * .6)));
        // Against the window, so the pointer goes from a row to its details without leaving the factions.
        double x = window.Right <= _size.Width - width ? window.Right : Math.Max(0, window.X - width);
        double y = Math.Clamp(window.Y, 0, Math.Max(0, _size.Height - height));
        var bounds = new Rect(Snap(x), Snap(y), width, height);
        ModernFrame("DIALOG_BOX_WINDOW", bounds, "mfaction/detail");
        Flush();
        double cx = bounds.X + pad, cy = bounds.Y + pad;
        string name = FactionName(faction.Name);
        ModernText("mfaction/detail/title", ModernEllipsis(name, textWidth - head - gap, title), new(cx, cy, textWidth - head, head), isAffiliated ? FactionAffiliatedColor : new(1, 1, 1), title);
        ModernCloseButton("mfaction/detail/close", new(bounds.Right - pad - head, cy, head, head));
        cy += head + gap;
        if (isAffiliated) { ModernText("mfaction/detail/affiliated", "You are affiliated with this faction.", new(cx, cy, textWidth, line), FactionAffiliatedColor, text); cy += line; }
        var (stance, stanceColor) = FactionStance(faction.Reaction);
        ModernText("mfaction/detail/reaction", "Reaction: " + FactionValue(faction.Reaction) + "  (" + stance + ")", new(cx, cy, textWidth, line), stanceColor, text);
        cy += line + gap * 2;
        double textBottom = bounds.Bottom - pad;
        int visible = Math.Max(1, (int)Math.Round((textBottom - cy) / line));
        // A longer text keeps clear of its scroll bar.
        if (lines.Count > visible) { textWidth -= ModernScrollbarRoom; lines = FactionDetailLines(faction.Name, description, textWidth, text); }
        _modernFactionScroll = Math.Clamp(_modernFactionScroll, 0, Math.Max(0, lines.Count - visible));
        Hit("mfaction/detail/text", FromPixels(new(cx, cy, textWidth, Math.Max(0, textBottom - cy))));
        // Two columns side by side (helps | harms): a name, its value at the column's right.
        double column = Math.Floor((textWidth - 16 * u) / 2), valueWidth = Math.Ceiling(ModernMeasure("-100%*", text) + 4 * u);
        for (int i = 0; i < visible && _modernFactionScroll + i < lines.Count; i++)
        {
            var l = lines[_modernFactionScroll + i];
            double ly = cy + i * line;
            string id = "mfaction/detail/t" + i.ToString(CultureInfo.InvariantCulture);
            if (!l.Columns) { ModernText(id, l.Left, new(cx, ly, textWidth, line), l.LeftColor, text); continue; }
            void Cell(string cell, string label, string? value, EnbRgb color, double left)
            {
                if (label.Length == 0) return;
                if (value is null) { ModernText(cell, ModernEllipsis(label, column, text), new(left, ly, column, line), color, text); return; }
                ModernText(cell, ModernEllipsis(label, column - valueWidth, text), new(left, ly, column - valueWidth, line), new(.92f, .94f, 1), text);
                ModernText(cell + "/value", value, new(left, ly, column, line), color, text, 2);
            }
            Cell(id + "/a", l.Left, l.LeftValue, l.LeftColor, cx);
            Cell(id + "/b", l.Right, l.RightValue, l.RightColor, cx + column + 16 * u);
        }
        ModernScrollbar("mfaction/detail/text", new(cx, cy, textWidth + ModernScrollbarRoom, visible * line), lines.Count, visible, _modernFactionScroll, v => _modernFactionScroll = v);
        Flush();
    }

    private static readonly EnbRgb FactionHeadingColor = new(1, .85f, .3f), FactionNoteColor = new(.7f, .74f, .85f), FactionNeutralColor = new(1, .72f, .25f),
        FactionHelpColor = new(.4f, .95f, .55f), FactionHarmColor = new(1, .42f, .42f);

    /// <summary>
    /// How a faction treats you at a reaction: hostile (attacks on sight) below -1,000, neutral up to 1,999, friendly from
    /// 2,000. Net7 Client Manager's standing bands (FactionStandingBandResolver); the retail panel shows only hated and
    /// loved halves.
    /// </summary>
    private const float FactionHostileBelow = -1000, FactionFriendlyFrom = 2000;
    private static (string Stance, EnbRgb Color) FactionStance(float reaction) =>
        reaction < FactionHostileBelow ? ("hostile", FactionHatedColor) : reaction >= FactionFriendlyFrom ? ("friendly", FactionAffiliatedColor) : ("neutral", FactionNeutralColor);

    private readonly record struct FactionLine(string Left, EnbRgb LeftColor, bool Columns = false, string? LeftValue = null,
        string Right = "", string? RightValue = null, EnbRgb RightColor = default);

    /// <summary>The details text: the known kill reactions as two columns (helps | harms), then the description.</summary>
    private List<FactionLine> FactionDetailLines(string key, string description, double width, double size)
    {
        var lines = new List<FactionLine>();
        string name = FactionName(key);
        void Wrapped(string words, EnbRgb color) { foreach (string l in ModernWrap(words, width, size)) lines.Add(new(l, color)); }
        static string Percent(EnbFactionKillReaction r) =>
            (r.Multiplier > 0 ? "+" : "") + (r.Multiplier * 100).ToString("0", CultureInfo.InvariantCulture) + "%" + (r.Estimated ? "*" : "");
        bool estimated = false;
        void Pairs(string leftHeading, string rightHeading, IEnumerable<EnbFactionKillReaction> helps, IEnumerable<EnbFactionKillReaction> harms, bool byKilled)
        {
            var left = helps.ToList(); var right = harms.ToList();
            lines.Add(new(leftHeading, FactionHelpColor, true, null, rightHeading, null, FactionHarmColor));
            for (int i = 0; i < Math.Max(left.Count, right.Count); i++)
            {
                string Name(EnbFactionKillReaction r) => FactionName(byKilled ? r.Killed : r.Affected);
                var a = i < left.Count ? left[i] : (EnbFactionKillReaction?)null;
                var b = i < right.Count ? right[i] : (EnbFactionKillReaction?)null;
                estimated |= a?.Estimated == true || b?.Estimated == true;
                lines.Add(new(a is { } x ? Name(x) : "", FactionHelpColor, true, a is { } xv ? Percent(xv) : null,
                    b is { } y ? Name(y) : "", b is { } yv ? Percent(yv) : null, FactionHarmColor));
            }
            if (left.Count == 0 && right.Count == 0) Wrapped("None known.", FactionNoteColor);
        }
        // What killing one of its ships does: the chart's column, its own loss included.
        var effects = EnbFactionKillReactions.All.Where(r => r.Killed == key).OrderByDescending(r => Math.Abs(r.Multiplier)).ThenBy(r => FactionName(r.Affected)).ToList();
        // Which other kills change its standing: the chart's row, its own ships left out.
        var changes = EnbFactionKillReactions.Changing(key).OrderByDescending(r => Math.Abs(r.Multiplier)).ThenBy(r => FactionName(r.Killed)).ToList();
        if (effects.Count == 0 && changes.Count == 0) Wrapped("No known kill reactions.", FactionNoteColor);
        else
        {
            if (effects.Count > 0)
            {
                Wrapped("Killing a " + name + " ship", FactionHeadingColor);
                Pairs("Raises", "Lowers", effects.Where(r => r.Multiplier > 0), effects.Where(r => r.Multiplier < 0), false);
                // The chart's net change: the column's sum, the loss with the faction itself included.
                float net = EnbFactionKillReactions.Net(key);
                Wrapped("Overall: " + (Math.Abs(net) < .01f ? "even" : Math.Abs(net).ToString("0.#", CultureInfo.InvariantCulture) + "x " + (net > 0 ? "gain" : "loss")),
                    Math.Abs(net) < .01f ? FactionNoteColor : net > 0 ? FactionHelpColor : FactionHarmColor);
            }
            if (changes.Count > 0)
            {
                lines.Add(new("", FactionNoteColor));
                Wrapped(name + " standing, when you kill another faction's ship", FactionHeadingColor);
                Pairs("Raised by killing", "Lowered by killing", changes.Where(r => r.Multiplier > 0), changes.Where(r => r.Multiplier < 0), true);
            }
            lines.Add(new("", FactionNoteColor));
            Wrapped("Percentages of what the kill costs with the killed ship's own faction (Net-7 Wiki faction chart)." +
                (estimated ? " * Estimated: no Alliance ships are known." : ""), FactionNoteColor);
        }
        if (description.Length > 0)
        {
            lines.Add(new("", FactionNoteColor));
            Wrapped(description.Replace("\\n", "\n"), new(1, 1, 1));
        }
        return lines;
    }

    private bool ActivateModernFactions(string id, bool right)
    {
        if (!id.StartsWith("mfaction/", StringComparison.Ordinal)) return false;
        if (right) return true;
        if (id == "mfaction/close") { _modernFactions = false; _modernFactionSelected = null; InteractionSoundRequested?.Invoke("TextClick03.wav"); return true; }
        if (id == "mfaction/detail/close") { _modernFactionSelected = null; InteractionSoundRequested?.Invoke("Roll_Select02b.wav"); return true; }
        if (id.StartsWith("mfaction/row/", StringComparison.Ordinal))
        {
            string key = id[13..];
            _modernFactionSelected = _modernFactionSelected == key ? null : key;
            _modernFactionScroll = 0;
            InteractionSoundRequested?.Invoke("fontclick.wav");
        }
        return true;
    }

    /// <summary>The wheel over the faction details scrolls its text.</summary>
    internal bool ScrollModernFaction(string id, double delta)
    {
        if (!_modernFactions || _modernFactionSelected is null || !id.StartsWith("mfaction/detail", StringComparison.Ordinal) || delta == 0) return false;
        _modernFactionScroll = Math.Max(0, _modernFactionScroll + (delta < 0 ? 3 : -3));
        Publish(); return true;
    }
}
