using System.Collections.Immutable;
using EnbNext.Rendering;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed class EnbMessageLineUi(Func<string, EnbRetailFontFace?> fontResolver) : IDisposable
{
    public EnbTimedMessageLineQueue Queue { get; } = new();
    private readonly EnbRetailText _label = new();
    private EnbUiSize _size;
    private double _scale;
    private EnbRetailFontFace? Font(string id) => fontResolver(id);
    public void SetViewport(EnbUiSize size, double scale) { _size = size; _scale = scale; }
    public EnbRgb Color(string id, EnbRgb fallback) => _messageLineColors.GetValueOrDefault(id, fallback);
    public void Dispose() => _label.Dispose();
    private readonly Dictionary<string, EnbInterfaceTextDefinition> _messageLineStyles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnbRgb> _messageLineColors = new(StringComparer.OrdinalIgnoreCase);
    private EnbRetailIniDocument? _messageFontArt;
    private EnbMessageFontEffects? _messageEffects;
    private long? _messageEffectsTime;
    private double _messageInputGap;
    internal Func<long> MessageClock { get; set; } = () => Environment.TickCount64;
    private EnbRetailEphemeralCatalog? _ephemeral;
    // The native 00BDBB84 generator is initialized with zero at 004441E0.
    // Keep its lifetime independent of a character's presentation stream.
    internal Func<int, int> EphemeralDraw { get; set; } = total =>
        EnbRetailPresentationRandom.Shared.Next(0,total);

    public void Load(EnbRetailAssetBundle? assets, EnbAuthoredUiAsset? chat)
    {
        _messageLineStyles.Clear(); _messageLineColors.Clear();
        _messageEffects = null; _messageEffectsTime = null;
        _messageInputGap = 0;
        _ephemeral = null;
        if (assets is null) return;
        string ephemeralPath = Path.Combine(assets.Paths.ClientIniDirectory, "ephemeral.ini");
        try { _ephemeral = EnbRetailEphemeralCatalog.Parse(EnbRetailIniDocument.Load(ephemeralPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (chat is not null)
        {
            var hierarchy = chat.Hierarchy;
            var input = hierarchy.Pivots.FirstOrDefault(p => p.Name.Equals("chat_vp_br", StringComparison.OrdinalIgnoreCase));
            var pane = hierarchy.Pivots.FirstOrDefault(p => p.Name.Equals("vp1_br", StringComparison.OrdinalIgnoreCase));
            if (input is not null && pane is not null &&
                EnbAuthoredTargetPanelLoader.TryResolveTranslation(hierarchy, input.Ordinal, out var inputPosition) &&
                EnbAuthoredTargetPanelLoader.TryResolveTranslation(hierarchy, pane.Ordinal, out var panePosition))
                _messageInputGap = panePosition.Y - inputPosition.Y;
        }
        var layout = EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory, "fc_line_display.ini"));
        _messageFontArt = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "fontart.ini"));
        foreach (var section in _messageFontArt.EnumerateSections().Select(s => s.Value))
            if (section.GetValueOrDefault("ColorID") is {} colorId && section.GetValueOrDefault("RGB")?.Split(',') is { Length: 3 } rgb)
            {
                _messageLineColors[colorId] = new(byte.Parse(rgb[0]) / 255f, byte.Parse(rgb[1]) / 255f, byte.Parse(rgb[2]) / 255f);
            }
        var definitions = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "fc_line_display.ini"));
        for (int i = 1; definitions.TryGetSection($"STR-{i}", out var section); i++)
            if (section.TryGetValue("StringID", out var name) && layout.TryResolve(name, out var style) &&
                EnbMessageFontEffects.TryCreate(style, _messageFontArt, out _)) _messageLineStyles[name] = style;
    }

    public void EnqueueNamed(string name)
    {
        if (_ephemeral?.Find(name) is not { } entry || entry.SelectStyle(EphemeralDraw(entry.TotalChance)) is not { } style) return;
        Enqueue(new(EnbNext.Net7.Presentation.Net7MessageLineKind.Priority, entry.Text, style, 0, 2));
    }

    public void Enqueue(EnbNext.Net7.Presentation.Net7MessageLineUpdate line)
    {
        if (!_messageLineStyles.TryGetValue(line.EphemeralType, out var style) ||
            style.Properties.GetValueOrDefault("FontType") is not {} fontId || Font(fontId) is not {} font) return;
        // 005A7690 wraps at 575 authored units; 005A72B0 adds continuation dots.
        var lines = EnbRetailDialogText.WrapChat(line.Text, (float)(575 * _size.Width * _scale / (640 * font.Scale)), c => font.CharacterAdvance(c)).ToArray();
        for (int i = 0; i < lines.Length; i++)
            Queue.Enqueue(line with { Text = (i > 0 ? "..." : "") + lines[i] + (i + 1 < lines.Length ? "..." : "") });
    }

    public bool Advance()
    {
        long now = MessageClock();
        float opacity = _messageEffects?.Opacity ?? 1;
        float verticalScale = _messageEffects?.VerticalScale ?? 1;
        string? effectText = _messageEffects?.DisplayText;
        _messageEffects?.Advance(_messageEffectsTime is {} last ? Math.Max(0, now - last) : 0);
        _messageEffectsTime = now;
        bool changed = Queue.Advance(now, _messageEffects?.Complete ?? true);
        if (changed || Queue.Current is null)
        {
            _messageEffects = null;
            if (Queue.Current is {} line && _messageFontArt is {} fontArt &&
                _messageLineStyles.TryGetValue(line.EphemeralType, out var style))
            {
                EnbMessageFontEffects.TryCreate(style, fontArt, out _messageEffects, Queue.DisplayText ?? line.Text, () => (int)EnbNext.Rendering.EnbRetailCrtRandomSource.Current.Next());
                _messageEffects?.Advance(0);
            }
        }
        return changed || opacity != (_messageEffects?.Opacity ?? 1) || verticalScale != (_messageEffects?.VerticalScale ?? 1) || effectText != _messageEffects?.DisplayText;
    }

    public EnbUiTextDraw? Draw(double chatBottom)
    {
        if (Queue.DisplayText is not {} text || Queue.Current is not {} line ||
            !_messageLineStyles.TryGetValue(line.EphemeralType, out var style) ||
            style.Properties.GetValueOrDefault("FontType") is not {} fontId || Font(fontId) is not {} font) return null;
        text = _messageEffects?.DisplayText ?? text;
        double width = font.MeasureWidth(text) * font.Scale;
        // 005A69B0 supplies the chat pane's lower anchor to MessageLine, which
        // is authored TOP,CENTER in fc_line_display.ini.
        bool chatAnchor = line.EphemeralType.Equals("MessageLine", StringComparison.OrdinalIgnoreCase) ||
            line.EphemeralType.Equals("Simple", StringComparison.OrdinalIgnoreCase);
        double top = chatAnchor ? chatBottom + _messageInputGap * _size.Width / 640 : _size.Height / 2;
        double left = _size.Width / 2;
        // 006D5170 converts normalized Offset to 640x480 scene units;
        // 006D64C0 adds it to the anchor. Scene Y is upward. Justification
        // subtracts the authored font dimensions from that point.
        if (style.Properties.GetValueOrDefault("Offset")?.Split(',') is { Length: 2 } offset &&
            double.TryParse(offset[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
            double.TryParse(offset[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
            double.IsFinite(x) && double.IsFinite(y))
        { left += x * _size.Width; top -= y * _size.Height; }
        string[] justify = style.Properties.GetValueOrDefault("Justify", "TOP,CENTER").Split(',', StringSplitOptions.TrimEntries);
        double height = font.CellHeight * font.Scale;
        if (justify[0] == "CENTER") top -= height / 2;
        else if (justify[0] == "BOTTOM") top -= height;
        if (justify.Length > 1 && justify[1] == "RIGHT") left -= width;
        else if (justify.Length < 2 || justify[1] != "LEFT") left -= width / 2;
        string colorId = (Queue.Replaying ? style.Properties.GetValueOrDefault("AltColor") : null)
            ?? style.Properties.GetValueOrDefault("Color", "");
        if (!_messageLineColors.TryGetValue(colorId, out var color)) return null;
        _label.SetText(text, font, color); _label.SetDisplayScale(1,1);
        return _label.Raster is { } raster ? new EnbUiTextDraw(raster, new(left, top, _label.Width, _label.Height))
        { Clip = new(left, top, width + 2, Math.Max(0, _size.Height - top)),
          Opacity = _messageEffects?.Opacity ?? 1, VerticalScale = _messageEffects?.VerticalScale ?? 1 } : null;
    }
}
