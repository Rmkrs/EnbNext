using EnbNext.Presentation;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI chat: a local line the player can click (the offer to send a lag report). The line reads as a link
/// until it is clicked once; the action runs and the line becomes plain text.
/// </summary>
internal sealed partial class EnbChatUi
{
    private readonly Dictionary<string, Action> _modernLinks = new(StringComparer.Ordinal);
    // The link of each drawn link row, by its hit id, so every row of a wrapped link lights together.
    private readonly Dictionary<string, string> _modernLinkHits = new(StringComparer.Ordinal);

    /// <summary>Next: Help > Send a report to the developer (both chat styles' Help menus).</summary>
    public Action? DeveloperReportRequested { get; set; }

    /// <summary>A chat line ending in <paramref name="text"/> becomes a link running <paramref name="action"/> once.</summary>
    internal void AddModernLink(string text, Action action) { _modernLinks[text] = action; Publish(); }

    private string? ModernLinkOf(EnbChatLine line)
    {
        if (_modernLinks.Count == 0) return null;
        foreach (string text in _modernLinks.Keys) if (line.Text.EndsWith(text, StringComparison.Ordinal)) return text;
        return null;
    }

    private void RunModernLink(string text)
    {
        if (!_modernLinks.Remove(text, out var action)) return;
        Publish();
        action();
    }
}
