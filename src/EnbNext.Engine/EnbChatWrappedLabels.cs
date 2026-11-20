using System.Globalization;
using System.Numerics;
using Rect = EnbNext.Presentation.EnbUiRect;
using Size = EnbNext.Presentation.EnbUiSize;
using Point = EnbNext.Presentation.EnbUiPoint;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed class EnbChatWrappedLabels(Func<Rect, Rect> DialogScreen, Action<string, Rect, EnbRgb, string, float, Rect> emit, Func<long> clock)
{
    private sealed record ChatWrappedLabel(string Text, Size Size, EnbRetailFontFace Font, double Scale, string[] Lines,
        EnbWrappedLabelRow[] Rows, EnbWrappedLabelOpening[] Opening, float ScrollSpeed)
    {
        public long? Tick;
    }
    private readonly Dictionary<string, ChatWrappedLabel> _dialogWrapped = [];

    internal void Clear() => _dialogWrapped.Clear();
    internal void Remove(string key) => _dialogWrapped.Remove(key);

    // 5E6BA0/5EB140 and the commands owner construct 6CF930 wrapped rows.
    // They do not use 6821F0's chat-message flags or its single-row shortcut.
    internal void Publish(string prefix, string id,
        IReadOnlyDictionary<string, string> properties, string text, Vector3 point,
        EnbRetailFontFace font, EnbRgb color, float opacity, EnbRetailAssetBundle assets, Size size, double scale,
        Rect bounds, string hover, Point pointer)
    {
        double Number(string name, double fallback = 0) => double.TryParse(properties.GetValueOrDefault(name),
            CultureInfo.InvariantCulture, out double value) ? value : fallback;
        double[] Pair(string name) => properties.GetValueOrDefault(name, "0,0").Split(',')
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var box = Pair("BoxOffset"); var offset = Pair("Offset");
        double xUnit = size.Width / 640, yUnit = size.Height / 480;
        double height = font.CellHeight * font.Scale;
        double step = height * Number("Spacing");
        double width = Number("Width");
        if (width <= 0) width = (int)(bounds.Right - 320) - (int)(bounds.Left - 320) - 2 * box[0];
        // 6CFB20 passes B750A8 ("...") to the font width virtual, not a space.
        double wrapWidth = width * xUnit / font.Scale - font.MeasureWidth("...");
        string key = prefix + "/" + id;
        if (!_dialogWrapped.TryGetValue(key, out var state) || state.Text != text || state.Size != size ||
            !ReferenceEquals(state.Font, font) || state.Scale != scale)
        {
            var lines = EnbRetailDialogText.Wrap(text.Length == 0 ? " " : text, (float)wrapWidth,
                c => font.CharacterAdvance(c));
            float wipe = 0, stay = -1, scroll = 0;
            var effects = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "fontart.ini"));
            foreach (var effect in effects.EnumerateSections().Select(section => section.Value))
            {
                string? effectId = effect.GetValueOrDefault("EffectID");
                if (effectId is null) continue;
                bool Uses(string name) => properties.GetValueOrDefault(name, "").Split(',').Any(value => value.Trim() == effectId);
                float Read(string name) => float.Parse(effect.GetValueOrDefault(name, "0"), CultureInfo.InvariantCulture);
                if (Uses("EffectOn") && effect.GetValueOrDefault("EffectType") == "WIPE" && effect.GetValueOrDefault("Direction") == "RIGHT") wipe = Read("Speed");
                if (Uses("EffectOn") && effect.GetValueOrDefault("EffectType") == "STAY") stay = Read("Time");
                if (Uses("MouseOver") && effect.GetValueOrDefault("EffectType") == "SCROLL" && effect.GetValueOrDefault("Direction") == "UP") scroll = Read("Speed");
            }
            state = new(text, size, font, scale, lines, lines.Select((_, i) => new EnbWrappedLabelRow(
                (int)((Number("Wait") + i * Number("TimeSpacing")) * 1000))).ToArray(),
                lines.Select(_ => new EnbWrappedLabelOpening(wipe, stay)).ToArray(), scroll);
            _dialogWrapped[key] = state;
        }
        // 6D5D40 clips against the named attachment, including moved dialogs.
        var moved = DialogScreen(bounds);
        double left = 320 + Math.Clamp((int)(moved.Left / xUnit - 320 + box[0]), -320, 320);
        double right = 320 + Math.Clamp((int)(moved.Right / xUnit - 320 - box[0]), -320, 320);
        double top = 240 - Math.Clamp((int)(240 - moved.Top / yUnit - box[1]), -240, 240);
        double bottom = 240 - Math.Clamp((int)(240 - moved.Bottom / yUnit + box[1]), -240, 240);
        var clip = new Rect(left * xUnit, top * yUnit, Math.Max(0, right - left) * xUnit, Math.Max(0, bottom - top) * yUnit);
        bool hovered = hover.Equals("chatdialog/" + prefix + "/" + properties["Attach"], StringComparison.OrdinalIgnoreCase) &&
            pointer.X > clip.Left && pointer.X < clip.Right && pointer.Y > clip.Top && pointer.Y < clip.Bottom;
        long now = clock();
        int delta = state.Tick is {} tick ? (int)Math.Max(0, now - tick) : 0; state.Tick = now;
        float limit = (float)Math.Max(0, Math.Abs(step / yUnit) * state.Lines.Length - (int)(bounds.Height - 2 * box[1]));
        var anchor = DialogScreen(new(point.X + 320 + offset[0] * 640, 240 - point.Y - offset[1] * 480, 0, 0));
        string justify = properties.GetValueOrDefault("Justify", "");
        for (int i = 0; i < state.Lines.Length; i++)
        {
            var sample = state.Rows[i].Advance(delta, hovered && !state.Opening[i].Active, limit, state.ScrollSpeed);
            if (!sample.Visible) continue;
            double y = anchor.Y - i * step - sample.Offset * yUnit;
            bool ellipsis = state.Rows[i].UpdateEllipsis((float)(-y / yUnit), (float)(-clip.Bottom / yUnit), (int)(height / yUnit), i + 1 == state.Lines.Length);
            string line = state.Lines[i] + (ellipsis ? "..." : "");
            double lineWidth = font.MeasureWidth(line) * font.Scale;
            double x = anchor.X;
            if (justify.EndsWith(",CENTER", StringComparison.OrdinalIgnoreCase)) x -= lineWidth / 2;
            else if (justify.EndsWith(",RIGHT", StringComparison.OrdinalIgnoreCase)) x -= lineWidth;
            if (justify.StartsWith("CENTER,", StringComparison.OrdinalIgnoreCase)) y -= height / 2;
            int extent = state.Opening[i].Advance(state.Rows[i].Clock, (int)Math.Round(lineWidth * scale));
            var area = new Rect(x, y, Math.Max(1, lineWidth), height);
            emit(line, area, color, properties["FontType"], opacity,
                clip.Intersect(new Rect(x, y, extent / scale, height)));
        }
    }
}
