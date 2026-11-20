using System.Collections.Immutable;
using System.Text;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>
/// Next Modern UI rich tooltip: what a hovered thing is, laid out as a small card instead of lines of text: an icon,
/// a title and subtitle, then sections of label/value rows or text, and a footer. The owner of the hovered thing
/// builds it from the same data its details window shows (an item's examination, a skill's details).
/// </summary>
public sealed record EnbTooltipCard(string Title)
{
    public EnbRgb TitleColor { get; init; } = new(1, 1, 1);
    public string? Subtitle { get; init; }
    public EnbRgb SubtitleColor { get; init; } = new(.55f, 1, .55f);
    public EnbUiRaster? Icon { get; init; }
    public EnbUiRaster? IconFrame { get; init; }
    public ImmutableArray<EnbTooltipCardSection> Sections { get; init; } = [];
    public string? Footer { get; init; }
    /// <summary>Next: the equipped items this one would replace, shown as cards beside it.</summary>
    public ImmutableArray<EnbTooltipCard> Compare { get; init; } = [];

    /// <summary>The card's words, so a layout can be kept while the same card is asked for again.</summary>
    public string Key()
    {
        var key = new StringBuilder(Title).Append('\u0001').Append(Subtitle).Append('\u0001').Append(Footer)
            .Append('\u0001').Append(Icon is null ? 0 : Icon.Width).Append('\u0001').Append(TitleColor);
        foreach (var section in Sections)
        {
            key.Append('\u0002').Append(section.Heading).Append('\u0001').Append(section.Text).Append('\u0001').Append(section.TextColor);
            foreach (var row in section.Rows) key.Append('\u0003').Append(row.Label).Append('\u0001').Append(row.Value);
            foreach (var line in section.Lines) key.Append('\u0005').Append(line.Text).Append('\u0001').Append(line.Color).Append(line.Detail);
        }
        foreach (var other in Compare) key.Append('\u0006').Append(other.Key());
        return key.ToString();
    }
}

/// <summary>One part of a tooltip card: a heading (may be empty), label/value rows, wrapped text and coloured lines.</summary>
public sealed record EnbTooltipCardSection(string Heading)
{
    public ImmutableArray<EnbTooltipCardRow> Rows { get; init; } = [];
    public string? Text { get; init; }
    public EnbRgb TextColor { get; init; } = new(.9f, .93f, 1);
    public ImmutableArray<EnbTooltipCardLine> Lines { get; init; } = [];
}

/// <summary>A wrapped line of its own colour; a detail line explains the line above it, indented.</summary>
public readonly record struct EnbTooltipCardLine(string Text, EnbRgb Color, bool Detail = false);

public readonly record struct EnbTooltipCardRow(string Label, string Value);
