using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI tooltips: the text an in-game tooltip asked for (EnbTooltipContent), laid out again in the Modern UI
/// style: the clear Modern UI font at a fixed size, a dark rounded panel with a thin rim, the first paragraph of a
/// longer tooltip as its title. A tooltip that carries a card (an item, a skill) is drawn as that card instead: icon,
/// title, sections and footer. It stands where the retail tooltip would (at the pointer, or above its gadget) and
/// follows the fixed tooltip position like the retail one. Text is drawn once per content and screen size; moving the
/// pointer only moves the panel.
/// </summary>
internal sealed class EnbModernTooltip
{
    private string? _key;
    private EnbUiSize _viewport;
    private double _scale, _width, _height;
    private ImmutableArray<EnbUiTextDraw> _lines = [];
    private EnbUiRaster? _background;
    // Next: an item card with equipped items to compare lays those cards out beside it.
    private EnbModernTooltip[]? _compare;

    internal EnbUiTooltipDraw? Create(EnbUiTooltipDraw source, EnbUiSize viewport, double scale)
    {
        if (source.Text is not { Length: > 0 } text || EnbModernText.Renderer is not { } renderer || scale <= 0) return null;
        string key = source.Card is { } card ? "\u0004" + card.Key() : text;
        if (key != _key || viewport != _viewport || scale != _scale)
        {
            _key = null;
            bool laidOut = source.Card is { } shown ? LayoutCard(shown, viewport, scale, renderer) : Layout(text, viewport, scale, renderer);
            if (!laidOut) return null;
            if (source.Card is { Compare.Length: > 0 } compared) AddCompared(compared, viewport, scale);
            _key = key; _viewport = viewport; _scale = scale;
        }
        double u = Unit(viewport);
        var (x, y) = source.Gadget is { } gadget
            ? (gadget.Center.X - _width / 2, gadget.Top - _height - 4 * u)
            : EnbCurrentRetailTooltipLayout.Place(source.Pointer.X, source.Pointer.Y, _width, _height, viewport.Width, viewport.Height);
        // A tall card pushed back on screen must not cover what it describes: it goes above the pointer, or below it.
        if (source.Gadget is null && source.Card is not null)
        {
            double cy = Math.Clamp(y, 0, Math.Max(0, viewport.Height - _height));
            if (source.Pointer.Y >= cy && source.Pointer.Y <= cy + _height)
                y = source.Pointer.Y - _height - 16 * u >= 0 ? source.Pointer.Y - _height - 16 * u : source.Pointer.Y + 24 * u;
        }
        x = Math.Round(Math.Clamp(x, 0, Math.Max(0, viewport.Width - _width)) * scale) / scale;
        y = Math.Round(Math.Clamp(y, 0, Math.Max(0, viewport.Height - _height)) * scale) / scale;
        return new(source.Asset, new(x, y, _width, _height), new(_width, _height))
        { Background = _background, Lines = _lines, Text = text, Pointer = source.Pointer, Gadget = source.Gadget, Card = source.Card };
    }

    private static double Unit(EnbUiSize viewport) => Math.Max(.75, viewport.Height / 1080);

    /// <summary>The compared cards to the right of the main one, each on its own panel, under one transparent backdrop.</summary>
    private void AddCompared(EnbTooltipCard card, EnbUiSize viewport, double scale)
    {
        double gap = Math.Round(6 * Unit(viewport)), x = _width, height = _height;
        var lines = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        if (_background is { } main) lines.Add(new(main, new(0, 0, _width, _height)));
        lines.AddRange(_lines);
        _compare ??= [new(), new()];
        for (int i = 0; i < Math.Min(_compare.Length, card.Compare.Length); i++)
        {
            if (_compare[i].CardLayout(card.Compare[i], viewport, scale) is not { } other) continue;
            x += gap;
            if (other.Background is { } panel) lines.Add(new(panel, new(x, 0, other.Width, other.Height)));
            foreach (var draw in other.Lines) lines.Add(draw with { Bounds = draw.Bounds with { X = draw.Bounds.X + x } });
            x += other.Width; height = Math.Max(height, other.Height);
        }
        _width = x; _height = height;
        _lines = lines.ToImmutable();
        _background = EnbModernIcons.Fill(new(0, 0, 0), 0);
    }

    /// <summary>A card laid out on its own (the item catalogue's details): its text and pictures from its top left
    /// corner, its panel and its size; laid out again only when the card or the screen changes.</summary>
    internal (ImmutableArray<EnbUiTextDraw> Lines, EnbUiRaster? Background, double Width, double Height)? CardLayout(EnbTooltipCard card, EnbUiSize viewport, double scale)
    {
        if (EnbModernText.Renderer is not { } renderer || scale <= 0) return null;
        string key = "\u0004" + card.Key();
        if (key != _key || viewport != _viewport || scale != _scale)
        {
            _key = null;
            if (!LayoutCard(card, viewport, scale, renderer)) return null;
            _key = key; _viewport = viewport; _scale = scale;
        }
        return (_lines, _background, _width, _height);
    }

    private static List<string> Wrap(IEnbModernTextRenderer renderer, string text, float px, double scale, double width)
    {
        var lines = new List<string>();
        foreach (string paragraph in text.Replace("\r", "", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string current = "";
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && renderer.Measure(candidate, px) / scale > width) { lines.Add(current); current = word; }
                else current = candidate;
            }
            lines.Add(current);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private bool Layout(string text, EnbUiSize viewport, double scale, IEnbModernTextRenderer renderer)
    {
        double u = Unit(viewport), size = 14 * u, pad = Math.Round(9 * u * scale) / scale, maxText = Math.Round(380 * u);
        float px = (float)(size * scale);
        // Wrapped to the widest line that fits; the retail tooltip shows at most twenty lines.
        string[] paragraphs = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var lines = new List<string>();
        int titleLines = 0;
        for (int p = 0; p < paragraphs.Length; p++)
        {
            var wrapped = Wrap(renderer, paragraphs[p], px, scale, maxText);
            if (wrapped.Count == 0) lines.Add(""); else lines.AddRange(wrapped);
            if (p == 0) titleLines = lines.Count;
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count > 20) lines.RemoveRange(20, lines.Count - 20);
        if (lines.Count == 0) return false;
        // A one-paragraph tooltip is all body text; a longer one has its first paragraph as the title.
        if (paragraphs.Length < 2 || titleLines >= lines.Count || paragraphs[0].Trim().Length == 0) titleLines = 0;
        double lineHeight = Math.Round(19 * u * scale) / scale, textWidth = 0;
        foreach (string line in lines) textWidth = Math.Max(textWidth, renderer.Measure(line, px) / scale);
        _width = Math.Ceiling((textWidth + pad * 2) * scale) / scale;
        _height = Math.Ceiling((lines.Count * lineHeight + pad * 2 - Math.Round(2 * u)) * scale) / scale;
        double padding = renderer.Padding(px) / scale, ink = renderer.InkCenterOffset("H", px) / scale;
        var draws = ImmutableArray.CreateBuilder<EnbUiTextDraw>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length == 0) continue;
            var color = i < titleLines ? new EnbRgb(1, .85f, .3f) : new EnbRgb(.9f, .93f, 1);
            if (renderer.Render(lines[i], px, color) is not { } raster) continue;
            double width = raster.Width / scale, height = raster.Height / scale;
            double centre = pad - Math.Round(u) + i * lineHeight + lineHeight / 2;
            draws.Add(new(raster, new(Math.Round((pad - padding) * scale) / scale, Math.Round((centre - height / 2 - ink) * scale) / scale, width, height)));
        }
        _lines = draws.ToImmutable();
        _background = EnbModernIcons.TooltipPanel((int)Math.Round(_width * scale), (int)Math.Round(_height * scale));
        return true;
    }

    /// <summary>A card: icon and title at the top, then each section (a thin rule, its heading, rows, text), then the footer.</summary>
    private bool LayoutCard(EnbTooltipCard card, EnbUiSize viewport, double scale, IEnbModernTextRenderer renderer)
    {
        double u = Unit(viewport), pad = Math.Round(11 * u), gap = Math.Round(6 * u), width = Math.Round(360 * u), inner = width - pad * 2;
        double line = Math.Round(18 * u), titleLine = Math.Round(21 * u), small = Math.Round(16 * u);
        float body = (float)(13 * u * scale), titlePx = (float)(16 * u * scale), headPx = (float)(12 * u * scale), footPx = (float)(12 * u * scale);
        var draws = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        double Snap(double v) => Math.Round(v * scale) / scale;
        // One line of text, its capitals centred on centreY; align 0 left, 2 right within [x, x + area].
        void Text(string text, float px, EnbRgb color, double x, double centreY, double area, int align = 0)
        {
            if (text.Length == 0) return;
            if (renderer.Measure(text, px) / scale > area)
            {
                while (text.Length > 1 && renderer.Measure(text + "...", px) / scale > area) text = text[..^1];
                text = text.TrimEnd() + "...";
            }
            if (renderer.Render(text, px, color) is not { } raster) return;
            double w = raster.Width / scale, h = raster.Height / scale, padding = renderer.Padding(px) / scale;
            double left = align == 2 ? x + area - (w - padding * 2) - padding : x - padding;
            draws.Add(new(raster, new(Snap(left), Snap(centreY - h / 2 - renderer.InkCenterOffset("H", px) / scale), w, h)));
        }
        void Rule(double y) => draws.Add(new(EnbModernIcons.Fill(new(.45f, .55f, .85f), 70), new(pad, Snap(y), inner, Math.Max(1, Math.Round(scale)) / scale)));

        // The header: icon in its frame, title (up to two lines) and subtitle.
        double y = pad, icon = card.Icon is null && card.IconFrame is null ? 0 : Math.Round(42 * u);
        if (icon > 0)
        {
            if (card.IconFrame is { } frame) draws.Add(new(frame, new(pad, y, icon, icon)));
            if (card.Icon is { } picture) draws.Add(new(picture, new(Snap(pad + 2 * u), Snap(y + 3 * u), icon - 4 * u, icon - 6 * u)));
        }
        double tx = pad + (icon > 0 ? icon + gap + 2 * u : 0), tw = width - pad - tx;
        var titleLines = Wrap(renderer, card.Title, titlePx, scale, tw);
        if (titleLines.Count > 2) { titleLines = titleLines.Take(2).ToList(); }
        double textBlock = titleLines.Count * titleLine + (card.Subtitle is { Length: > 0 } ? line : 0);
        double ty = y + Math.Max(0, (icon - textBlock) / 2);
        foreach (string title in titleLines) { Text(title, titlePx, card.TitleColor, tx, ty + titleLine / 2, tw); ty += titleLine; }
        if (card.Subtitle is { Length: > 0 } subtitle) Text(subtitle, body, card.SubtitleColor, tx, ty + line / 2, tw);
        y += Math.Max(icon, textBlock) + gap;

        // The sections.
        var muted = new EnbRgb(.76f, .8f, .9f);
        foreach (var section in card.Sections)
        {
            var textLines = section.Text is { Length: > 0 } sectionText ? Wrap(renderer, sectionText, body, scale, inner) : [];
            if (textLines.Count > 16) { textLines = textLines.Take(16).ToList(); textLines[^1] = textLines[^1] + "..."; }
            if (section.Rows.Length == 0 && textLines.Count == 0 && section.Lines.Length == 0) continue;
            Rule(y); y += gap;
            if (section.Heading.Length > 0) { Text(section.Heading, headPx, new(.7f, .8f, 1), pad, y + small / 2, inner); y += small; }
            foreach (var row in section.Rows)
            {
                double labelWidth = inner * .58;
                Text(row.Label, body, muted, pad, y + line / 2, labelWidth - 4 * u);
                Text(row.Value, body, new(1, 1, 1), pad + labelWidth, y + line / 2, inner - labelWidth, 2);
                y += line;
            }
            foreach (string text in textLines) { Text(text, body, section.TextColor, pad, y + line / 2, inner); y += line; }
            double indent = Math.Round(12 * u);
            foreach (var entry in section.Lines)
            {
                double at = entry.Detail ? pad + indent : pad, room = entry.Detail ? inner - indent : inner;
                foreach (string text in Wrap(renderer, entry.Text, body, scale, room)) { Text(text, body, entry.Color, at, y + line / 2, room); y += line; }
            }
            y += Math.Round(gap / 2);
        }
        if (card.Footer is { Length: > 0 } footer)
        {
            Rule(y); y += gap;
            foreach (string text in Wrap(renderer, footer, footPx, scale, inner)) { Text(text, footPx, new(.62f, .67f, .78f), pad, y + small / 2, inner); y += small; }
        }
        _width = width;
        _height = Math.Ceiling((y + pad - Math.Round(gap / 2)) * scale) / scale;
        _lines = draws.ToImmutable();
        _background = EnbModernIcons.TooltipPanel((int)Math.Round(_width * scale), (int)Math.Round(_height * scale));
        return true;
    }
}
