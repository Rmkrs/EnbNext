using System.Globalization;
using System.Text;
using EnbNext.Assets;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI NPC conversation: the retail talk window's content (speaker, the NPC's face and speech, the replies,
/// More or Done, Skill, Trade, close) in a Modern UI window. The NPC's text keeps the retail pages (six retail lines
/// each, which the face's speech follows) but is set in the clear font and wrapped to the window; replies are shown
/// in full instead of one truncated line, and each sends the retail reply request. Retail mode keeps the talk window.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    /// <summary>Where the dialogue window was dragged (relative to the screen); null keeps it centred near the top.</summary>
    internal EnbUiPoint? ModernTalkPosition { get; set; }
    internal Action<EnbUiPoint>? ModernTalkMoved { get; set; }
    private Rect _modernTalkBounds;
    /// <summary>A Modern UI window's right-click menu (owner, heading, entries, where).</summary>
    internal Action<string, string?, IReadOnlyList<EnbModernMenuItem>, EnbUiPoint>? ModernMenuRequested { get; set; }
    /// <summary>The dialogue's text size (1 = the Modern UI size, 1.15 the default); a Next preference set from its right-click menu.</summary>
    internal double ModernTalkTextScale { get; set; } = DefaultTalkTextScale;
    internal const double DefaultTalkTextScale = 1.15;
    internal Action<double>? ModernTalkTextScaleChanged { get; set; }
    private static readonly double[] TalkTextScales = [1, 1.15, 1.3, 1.5];

    /// <summary>A right click anywhere on the dialogue window opens its menu: the text size.</summary>
    private bool ModernTalkMenu(string id, bool right)
    {
        if (!right || !ModernUi) return false;
        bool talk = _conversationOwner is not null && !_trading && id.StartsWith("talk", StringComparison.Ordinal);
        if (!talk && !(_recovery is not null && id.StartsWith("recovery", StringComparison.Ordinal))) return false;
        int level = Array.FindLastIndex(TalkTextScales, s => s <= ModernTalkTextScale + 1e-6);
        if (level < 0) level = 0;
        void Set(int to) { ModernTalkTextScale = TalkTextScales[to]; ModernTalkTextScaleChanged?.Invoke(ModernTalkTextScale); Publish(); }
        ModernMenuRequested?.Invoke("talk/menu", "Dialogue",
        [
            EnbModernMenuItem.Heading("Text size " + Math.Round(TalkTextScales[level] * 100).ToString(CultureInfo.InvariantCulture) + "%"),
            new("Larger text", () => Set(level + 1), Enabled: level < TalkTextScales.Length - 1),
            new("Smaller text", () => Set(level - 1), Enabled: level > 0),
            new("Default size", () => Set(1), Enabled: level != 1),
        ], _point);
        return true;
    }
    private void ModernConversationPanel()
    {
        Flush();
        // As ConversationPanel: the portrait and its speech page.
        _dialoguePortrait ??= Conversation?.Portrait;
        var speechPage = DialogueSpeechPage;
        if (!ReferenceEquals(_portraitSeenPage, speechPage)) { _portraitSeenPage = speechPage; _portraitSpeechPage = _dialoguePortrait is null ? null : speechPage; }
        var tree = _dialogueHasText ? _dialogueTree as EnbNext.Net7.Presentation.Net7TalkTreeUpdate : null;
        var retailLines = DialogueLines(tree?.NpcSay ?? "");
        _talkPageCount = Math.Max(1, (retailLines.Length + 5) / 6);
        bool more = (_talkPage + 1) * 6 < retailLines.Length;

        // This page's text (blank retail lines are paragraph breaks).
        var page = new StringBuilder();
        foreach (string raw in retailLines.Skip(_talkPage * 6).Take(6))
        {
            string shown = EnbStationDialogueText.Display(raw);
            if (shown.Trim().Length == 0) { if (page.Length > 0 && page[^1] != '\n') page.Append('\n'); continue; }
            if (page.Length > 0 && page[^1] != '\n') page.Append(' ');
            page.Append(shown.Trim());
        }
        // The replies, on the last page, in full.
        var replies = new List<string>();
        if (tree is not null && !_conversationWaiting && !more)
            for (int i = 0; i < Math.Min(5, tree.Responses.Count); i++) replies.Add(tree.Responses[i].Text);
        // The buttons: More or Done while there is more to read (or nothing to answer), Skill and Trade when offered.
        var buttons = new List<(string Id, string Label)>();
        if (tree is not null && !_conversationWaiting && (more || tree.Responses.Count == 0))
            buttons.Add((more ? "talkMore" : "talkDone", more ? "More" : _dialogueDone ? "Done" : "Continue"));
        if (Conversation?.TradeAvailable == true) buttons.Add(("talkTrade", "Trade"));
        if (Conversation?.SkillAvailable == true) buttons.Add(("talkSkill", "Skill"));
        string speaker = _dialoguePortrait is { } p ? string.Join(" ", new[] { p.FirstName, p.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))) : _conversationName;
        ModernDialogueWindow("talk", speaker, _dialoguePortrait is null ? null : inner =>
            _draws[^1] = _draws[^1] with { Portrait = _dialoguePortrait, PortraitEnvironment = _scene!.Environment, PortraitCacheKey = "conversation/avatar",
                PortraitSpeech = _portraitSpeechPage, PortraitBounds = inner, PortraitAspect = inner.Width / inner.Height },
            page.ToString(), replies, "talkReply/", "talkClose", buttons);
    }

    /// <summary>
    /// Next Modern UI recovery dialogue (incapacitated in space): the retail TLI01A_AV content (the rescuer's name and
    /// animated face, the opening text, up to three answers; after an answer its follow-up text) in the Modern UI
    /// dialogue window. The answers and close send the same retail choice as RecoveryPanel; only the look differs.
    /// </summary>
    private void ModernRecoveryPanel()
    {
        if (_recovery is not { } details) return;
        Flush();
        var avatar = _scene?.RecoveryAvatar;
        string speaker = avatar is null ? "" : string.Join(" ", new[] { avatar.FirstName, avatar.LastName }.Where(s => !string.IsNullOrEmpty(s)));
        var text = new StringBuilder();
        foreach (string raw in DialogueLines(_recoveryText))
        {
            string shown = EnbStationDialogueText.Display(raw);
            if (shown.Trim().Length == 0) { if (text.Length > 0 && text[^1] != '\n') text.Append('\n'); continue; }
            if (text.Length > 0 && text[^1] != '\n') text.Append(' ');
            text.Append(shown.Trim());
        }
        // As RecoveryPanel: the answers until one is chosen (an empty answer keeps its place and is not offered).
        var answers = new List<string>();
        if (_recoveryChoice < 0)
            for (int i = 0; i < 3; i++) answers.Add(details.RemainingOuterStrings[1 + i]);
        ModernDialogueWindow("recovery", speaker, avatar is null ? null : inner =>
            _draws[^1] = _draws[^1] with { Portrait = avatar, PortraitEnvironment = _scene!.Environment, PortraitCacheKey = "recovery/avatar",
                PortraitSharedActor = true, PortraitPose = _recoveryPose, PortraitBounds = inner, PortraitAspect = inner.Width / inner.Height },
            text.ToString(), answers, "recoveryChoice/", "recoveryClose", []);
    }

    /// <summary>
    /// The Modern UI dialogue window shared by NPC conversations and the recovery dialogue: title and close button,
    /// the face (when there is one) beside the text, numbered replies in full, buttons at the bottom right and the
    /// error line. It is dragged by its body (one place for both) and its text size is the dialogue's setting.
    /// </summary>
    private void ModernDialogueWindow(string prefix, string speaker, Action<Rect>? face, string body, IReadOnlyList<string> replies,
        string replyPrefix, string closeId, IReadOnlyList<(string Id, string Label)> buttons)
    {
        double u = ModernUnit, k = Math.Clamp(ModernTalkTextScale, 1, 2), pad = Math.Round(14 * u), gap = Math.Round(10 * u);
        double text = 14 * u * k, small = 13 * u * k, title = 17 * u * k;
        double line = Math.Round(20 * u * k), head = Math.Round(28 * u * k), button = Math.Round(30 * u * k), close = Math.Round(22 * u);
        // Larger text widens the window with it (up to the screen), so a page keeps about the same lines.
        double width = Math.Round(Math.Min(640 * u * k, Math.Max(640 * u, _size.Width - 2 * pad)));
        bool hasFace = face is not null;
        double faceWidth = hasFace ? Math.Round(136 * u) : 0, faceHeight = hasFace ? Math.Round(164 * u) : 0;
        double column = width - pad * 2 - (hasFace ? faceWidth + gap : 0);

        var speech = body.Length > 0 ? ModernWrap(body, column, text) : [];
        var wrapped = new List<List<string>>();
        foreach (string reply in replies) wrapped.Add(reply.Length > 0 ? ModernWrap(reply, column - 30 * u, text) : []);
        int shown = wrapped.Count(r => r.Count > 0);
        double repliesHeight = wrapped.Where(r => r.Count > 0).Sum(r => r.Count * line + 8 * u) + (shown > 0 ? gap : 0);
        double content = Math.Max(faceHeight, speech.Count * line + repliesHeight);
        double height = pad + head + gap + content + (buttons.Count > 0 ? gap + button : 0) + (_message is null ? 0 : gap + line) + pad;
        double x = (_size.Width - width) / 2, y = Math.Max(0, _size.Height * .1);
        if (ModernTalkPosition is { } at) { x = at.X * _size.Width; y = at.Y * _size.Height; }
        // The window grows with the page; it stays whole on screen wherever it was left.
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        _modernTalkBounds = new(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", _modernTalkBounds, prefix + "Frame/modern");

        ModernText(prefix + "Name", ModernEllipsis(speaker, width - pad * 2 - close - gap, title), new(x + pad, y + pad, width - pad * 2 - close - gap, head), new(1, .85f, .3f), title);
        ModernCloseButton(closeId, new(x + width - pad - close, y + pad + (head - close) / 2, close, close));
        double top = y + pad + head + gap;

        // The speaker's face, in an inset box.
        if (face is not null)
        {
            var box = new Rect(x + pad, top, faceWidth, faceHeight);
            ModernFrame("DIALOG_BOX_INFO", box);
            var inner = new Rect(box.X + 3 * u, box.Y + 3 * u, box.Width - 6 * u, box.Height - 6 * u);
            if (_draws.Count > 0) face(inner);
        }
        Flush();
        double cx = x + pad + (hasFace ? faceWidth + gap : 0), cy = top;
        for (int i = 0; i < speech.Count; i++)
            ModernText(prefix + "Body/" + i.ToString(CultureInfo.InvariantCulture), speech[i], new(cx, cy + i * line, column, line), new(.92f, .94f, 1), text);
        cy += speech.Count * line;

        // Replies: each a row (number, full text); hover lights it; a click sends the retail request.
        if (shown > 0)
        {
            cy += gap;
            int number = 0;
            for (int i = 0; i < wrapped.Count; i++)
            {
                if (wrapped[i].Count == 0) continue;
                string id = replyPrefix + i.ToString(CultureInfo.InvariantCulture);
                double rh = wrapped[i].Count * line + 4 * u;
                var r = new Rect(cx - 4 * u, cy, column + 8 * u, rh);
                bool enabled = !_busy, hot = enabled && _hover == id;
                if (enabled) Hit(id, FromPixels(r));
                if (hot) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(r.Width * _scale), (int)Math.Round(r.Height * _scale)), r));
                EnbRgb color = !enabled ? new(.5f, .55f, .62f) : hot ? new(1, 1, .45f) : new(.55f, .82f, 1);
                ModernText(id + "/n", (++number).ToString(CultureInfo.InvariantCulture) + ".", new(cx, cy + 2 * u, 24 * u, line), new(.6f, .66f, .78f), text);
                for (int n = 0; n < wrapped[i].Count; n++)
                    ModernText(id + "/" + n.ToString(CultureInfo.InvariantCulture), wrapped[i][n], new(cx + 26 * u, cy + 2 * u + n * line, column - 26 * u, line), color, text);
                cy += rh + 4 * u;
            }
        }

        double by = y + height - pad - button - (_message is null ? 0 : gap + line), bx = x + width - pad;
        foreach (var (id, label) in buttons)
        {
            double bw = Math.Round(Math.Max(96 * u, ModernMeasure(label, text) + 28 * u));
            bx -= bw; ModernButton(id, label, new(bx, by, bw, button), text, "", !_busy); bx -= gap;
        }
        if (_message is not null)
            ModernText(prefix + "Error", ModernEllipsis(_message, width - pad * 2, small), new(x + pad, y + height - pad - line, width - pad * 2, line), new(1, .5f, .5f), small);
        Flush();
    }
}
