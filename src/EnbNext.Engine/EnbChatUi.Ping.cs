using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using EnbNext.Presentation;
using System.Numerics;
using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbChatUi
{
    private EnbAuthoredUiAsset? _pingAsset;
    private Rect _pingBounds, _pingValueBounds, _pingColorBounds;
    private EnbInterfaceLayoutCatalog? _pingLayout;
    private EnbRetailIniDocument? _pingColors;
    private bool _pingVisible, _pingDetails, _pingTooltipShown;
    private readonly Dictionary<string, EnbAuthoredTooltipFrame> _pingFrames = [];
    private readonly Dictionary<string, (Size Size, EnbAuthoredUiAsset Asset)> _pingFrameAssets = [];
    private readonly EnbCurrentRetailTooltipDelay _pingDwell = new();
    private readonly List<EnbRetailText> _pingTips = [];
    private string _pingTooltip = "";
    private readonly Dictionary<string, string> _chatTooltips = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EnbRgb> _chatLabelColors = new(StringComparer.OrdinalIgnoreCase);
    private float _chatCycleSpeed = 1;
    private int _chatBlinkPeriod = 500;
    private Point _pingPointer;
    public event Action<EnbUiTooltipDraw?>? PingTooltipChanged;
    public bool PerformanceVisible=>_pingVisible;
    public bool UsePerformanceOverlay { get; set; }

    public void TogglePing()
    {
        if (!_enabled || _scene?.IsTransitioning == true) return;
        _pingVisible = !_pingVisible; Publish(); UpdatePingTooltip();
    }

    private void TogglePingDetails() { _pingDetails = !_pingDetails; Publish(); }

    private void LoadPing()
    {
        _pingFrames.Clear(); _pingFrameAssets.Clear(); _chatTooltips.Clear(); _chatLabelColors.Clear(); _pingAsset = null;
        if (_assets is null) return;
        _pingAsset = EnbAuthoredUiAsset.Load(_assets, "tsi05c.w3d");
        if (_pingAsset is null) return;
        _pingColorBounds = EnbCockpitUi.MeshBounds(_pingAsset, "COLOR");
        _pingValueBounds = EnbCockpitUi.MeshBounds(_pingAsset, "VALUE");
        _pingBounds = _pingColorBounds.Union(_pingValueBounds);
        _pingLayout = EnbInterfaceLayoutCatalog.Load(Path.Combine(_assets.Paths.ClientIniDirectory, "fc_ping.ini"));
        _pingColors = EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory, "fontart.ini"));
        foreach (var (_, values) in _pingColors.EnumerateSections())
        {
            if (values.TryGetValue("ColorID", out string? id) && values.TryGetValue("RGB", out string? rgb) &&
                rgb.Split(',') is { Length: 3 } parts && byte.TryParse(parts[0], out byte red) &&
                byte.TryParse(parts[1], out byte green) && byte.TryParse(parts[2], out byte blue))
                _chatLabelColors[id] = new(red / 255f, green / 255f, blue / 255f);
            if (values.GetValueOrDefault("EffectID") == "FA_CYCLE" &&
                float.TryParse(values.GetValueOrDefault("Speed"), System.Globalization.CultureInfo.InvariantCulture, out float speed))
                _chatCycleSpeed = speed;
            if (values.GetValueOrDefault("EffectID") == "FA_BLINK" &&
                float.TryParse(values.GetValueOrDefault("Speed"), System.Globalization.CultureInfo.InvariantCulture, out float blinkSpeed) && blinkSpeed > 0)
                _chatBlinkPeriod = Math.Max(1, (int)(500 / blinkSpeed));
        }
        var layout = EnbInterfaceLayoutCatalog.Load(Path.Combine(_assets.Paths.ClientIniDirectory, "fc_default.ini"));
        var strings = EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory, "tooltip.ini"));
        var windows = EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory, "dynawin.ini"));
        if (strings.TryGetSection("UI_PING", out var ping)) _pingTooltip = ping.GetValueOrDefault("PING", "").Replace("\\n", "\n");
        foreach (var (section, category) in new[] { ("UI_COMBO_BOX", "tab/"), ("UI_COMMANDS", "chatdialog/commands/"), ("UI_PLAYEROPT", "chatdialog/ignore/"), ("UI_RADIO_MAIN", "chatdialog/private/"), ("UI_RADIO_ADMIN", "chatdialog/privatemembers/") })
            if (strings.TryGetSection(section, out var values))
                foreach (var (key, value) in values) _chatTooltips[category + key] = value.Replace("\\n", "\n").Trim();
        if (strings.TryGetSection("UI_STRINGENTRY", out var entryTips))
            foreach (var (key, value) in entryTips)
                _chatTooltips["chatdialog/ignore/entry/" + key.ToLowerInvariant()] = value.Replace("\\n", "\n").Trim();
        foreach (string name in new[] { "DIALOG_BOX_WINDOW", "DIALOG_BOX_INFO", "DIALOG_BOX_OPTION",
            "SYMBOL_CHAT_WINDOW", "SYMBOL_CHAT_SECTION", "TOOLTIPS" })
            if (EnbRetailScannerTooltipDefinition.Parse(layout, strings, windows, name) is { } d &&
                EnbAuthoredTooltipFrame.Load(_assets, d) is { } frame) _pingFrames[name] = frame;
    }

    private Rect PingScreen(Rect r) => new(r.X * _size.Width / 640, r.Y * _size.Height / 480,
        r.Width * _size.Width / 640, r.Height * _size.Height / 480);

    private void PublishPing()
    {
        if (UsePerformanceOverlay || !_pingVisible || _pingAsset is null || _pingLayout is null) return;
        _draws.Add(new(_pingAsset, 0, (PingScreen(_pingBounds)), false)
            { LayoutGroup=EnbUiGroup.Chat, DesignBounds = (_pingBounds.Translate(-320, -240)) });
        // Current retail TCP's extended-statistics provider returns false.
        // Preserve authored initial text; time-sync RTT is a different value.
        PingText("PING_EFFICIENCY", null, _pingValueBounds, true);
        Hit("ping/color", PingScreen(_pingColorBounds), TogglePingDetails);
        Hit("ping/value", PingScreen(_pingValueBounds), () => { });
        if (!_pingDetails || !_pingFrames.TryGetValue("DIALOG_BOX_WINDOW", out var outer) ||
            !_pingFrames.TryGetValue("DIALOG_BOX_INFO", out var info)) return;
        string[] lines = ["Latency : 000", "Data Vol : 000", "Efficiency : 000"];
        string heading = _pingLayout.TryResolve("HEADER_INFO", out var header) ? header.Properties.GetValueOrDefault("String0", "") : "";
        string fontId = header?.Properties.GetValueOrDefault("FontType", "FONT_SMALL") ?? "FONT_SMALL";
        if (Font(fontId) is not { } font) return;
        double unit = _size.Width / 640, uy = _size.Height / 480;
        double rowHeight = font.CellHeight  / uy;
        double inner = Math.Max(info.Definition.MinWidth,
            lines.Append(heading).Max(s => font.MeasureWidth(s))  / unit + 2 * info.Definition.BorderX);
        double titleHeight = Math.Max(info.Definition.MinHeight, rowHeight + 2 * info.Definition.BorderY);
        double bodyHeight = Math.Max(info.Definition.MinHeight, 3 * rowHeight + 2 * info.Definition.BorderY);
        double width = inner + 2 * outer.Definition.BorderX;
        double height = titleHeight + bodyHeight + 2 * outer.Definition.BorderY;
        // 005D855B/63 passes authored (-320,180), i.e. screen (0,60),
        // as a center to 00517C50, which clamps the top-left with margin 5.
        double x = 5, y = Math.Max(5, 60 - height / 2);
        var panel = new Rect(x, y, width, height);
        PingFrame("outer", outer, new(x, y, width, height));
        x += outer.Definition.BorderX; y += outer.Definition.BorderY;
        PingFrame("header", info, new(x, y, inner, titleHeight));
        PingText("HEADER_INFO", heading, new(x, y, inner, titleHeight), true);
        y += titleHeight;
        PingFrame("body", info, new(x, y, inner, bodyHeight));
        for (int i = 0; i < lines.Length; i++)
            PingText("PING_INFO_" + i, lines[i], new(x + info.Definition.BorderX,
                y + info.Definition.BorderY + i * rowHeight, inner - 2 * info.Definition.BorderX, rowHeight), false);
        Hit("ping/details", PingScreen(panel), () => { });
    }

    private void PingFrame(string key, EnbAuthoredTooltipFrame frame, Rect r, uint verb = 0)
    {
        if (!_pingFrameAssets.TryGetValue(key, out var cached) || cached.Size != r.Size)
            _pingFrameAssets[key] = cached = (r.Size, new(frame.Resource,
                EnbAuthoredUiAsset.Load(_assets!, frame.Definition.MeshFile + ".w3d")!.Hierarchy)
                { Geometry = frame.Geometry((float)r.Width, (float)r.Height) });
        var geometry = cached.Asset.Geometry!;
        var min = geometry.Positions.Aggregate(Vector3.Min); var max = geometry.Positions.Aggregate(Vector3.Max);
        _draws.Add(new(cached.Asset, verb, (PingScreen(r)), false)
            { LayoutGroup=EnbUiGroup.Chat, DesignBounds = new(min.X, -max.Y, max.X - min.X, max.Y - min.Y) });
    }

    private void PingText(string id, string? value, Rect bounds, bool centered)
    {
        if (_pingLayout is null || !_pingLayout.TryResolve(id, out var definition) || _pingColors is null) return;
        value ??= definition.Properties.GetValueOrDefault("String0", "");
        string fontId = definition.Properties.GetValueOrDefault("FontType", "FONT_SMALL");
        if (Font(fontId) is not { } font) return;
        var r = PingScreen(bounds);
        if (centered) { double w = font.MeasureWidth(value) ; r = new(r.X + (r.Width - w) / 2, r.Y, w + 1, r.Height); }
        var colorId = definition.Properties.GetValueOrDefault("Color");
        foreach (var section in _pingColors.EnumerateSections().Select(s => s.Value))
            if (section.GetValueOrDefault("ColorID") == colorId && section.GetValueOrDefault("RGB")?.Split(',') is { Length: 3 } rgb)
            { Text(value, r, new(byte.Parse(rgb[0]) / 255f, byte.Parse(rgb[1]) / 255f, byte.Parse(rgb[2]) / 255f), fontId, true); break; }
    }

    public void SetImmediateTooltips(bool value) { _pingDwell.Immediate = value; UpdatePingTooltip(); }
    private void UpdatePingTooltip()
    {
        string tooltip = _hover is "ping/color" or "ping/value" && _pingVisible ? _pingTooltip :
            _chatTooltips.GetValueOrDefault(_hover, "");
        if (_hover.StartsWith("chatdialog/private/BUTTON", StringComparison.Ordinal) &&
            int.TryParse(_hover.AsSpan("chatdialog/private/BUTTON".Length), out int button))
            tooltip = _chatTooltips.GetValueOrDefault("chatdialog/private/" + PrivatePanelLabel("BUTTON" + button.ToString("00")), "");
        if (!_enabled || tooltip.Length == 0)
        {
            _pingDwell.Reset();
            if (_pingTooltipShown) { _pingTooltipShown = false; PingTooltipChanged?.Invoke(null); }
            return;
        }
        if (!_pingFrames.TryGetValue("TOOLTIPS", out var frame) || Font(frame.Definition.FontId) is not { } font ||
            !_pingDwell.IsReady(1, Environment.TickCount64, EnbCurrentRetailTooltipDelay.Milliseconds(
                _assets!.ResolvePlayerOptions(_session()).Options.InterfaceTooltipDelay))) return;
        var d = frame.Definition;
        // 006ABC60 fits at 100 design units, growing by 50 up to 600
        // until line count <= attempt count or the explicit-line count.
        string[] lines = [];
        int explicitLines = tooltip.Count(c => c == '\n') + 1;
        for (int attempt = 1, width = 100; attempt <= 20; attempt++, width = Math.Min(600, width + 50))
        {
            // 004FAB70 -> 006BCD20: glyph pixels * font scale * 640 / display width.
            lines = Wrap(tooltip, font, width * _size.Width / 640 / font.Scale).Take(20).ToArray();
            if (lines.Length <= attempt || lines.Length <= explicitLines) break;
        }
        while (_pingTips.Count < lines.Length) _pingTips.Add(new());
        bool disabledIgnoreButton = _hover.StartsWith("chatdialog/ignore/BUTTON", StringComparison.OrdinalIgnoreCase) &&
            !DialogPieceEnabled("ignore", _hover["chatdialog/ignore/".Length..]);
        disabledIgnoreButton |= _hover.StartsWith("chatdialog/privatemembers/BUTTON", StringComparison.OrdinalIgnoreCase) &&
            !DialogPieceEnabled("privatemembers", _hover["chatdialog/privatemembers/".Length..]);
        for (int i = 0; i < lines.Length; i++)
        {
            _pingTips[i].SetText(lines[i], font, disabledIgnoreButton ? new(1, .1f, .1f) : new(.1f, 1, .1f));
            _pingTips[i].SetDisplayScale(1,1);
        }
        double sx = _size.Width / 640, sy = _size.Height / 480;
        double w = Math.Max(d.MinWidth * sx, _pingTips.Take(lines.Length).Max(t => t.Width) + 2 * d.BorderX * sx);
        double textHeight = _pingTips.Take(lines.Length).Sum(t => t.Height);
        double h = Math.Max(d.MinHeight * sy, textHeight + 2 * d.BorderY * sy);
        var screenPointer=UiLayout?.Placement(EnbUiGroup.Chat).Apply(_pingPointer,_size)??_pingPointer;
        var p = EnbCurrentRetailTooltipLayout.Place(screenPointer.X, screenPointer.Y, w, h, _size.Width, _size.Height);
        var text = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        double y = (h - textHeight) / 2;
        foreach (var line in _pingTips.Take(lines.Length))
        {
            if (line.Raster is { } raster) text.Add(new(raster, new((w - line.Width) / 2, y, line.Width, line.Height)));
            y += line.Height;
        }
        _pingTooltipShown = true;
        PingTooltipChanged?.Invoke(new(frame, new(p.X, p.Y, w, h), new(w / sx, h / sy))
            { Lines = text.ToImmutable() });
    }
}
