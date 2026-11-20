using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private readonly Dictionary<string, EnbRgb> _confirmationColors = [];
    private void StandardConfirmationDialog(string prefix, string message,
        IReadOnlyDictionary<string, string>? definition, string[] actions, bool enabled = true, bool qualifyActions = true,
        Point? origin = null, string[]? authoredLines = null)
    {
        bool small = definition?.GetValueOrDefault("FontSize") == "FONT_SMALL";
        string fontId = small ? "FONT_SMALL" : "FONT_MED", optionFontId = "FONT_SMALL";
        EnbRgb infoColor = small ? new(1, 1, 1) : new(.75f, .75f, 1), optionColor = new(1, 1, 0);
        if (_groupDialogLabels?.TryResolve(small ? "DIALOG_INFO_SMALL" : "DIALOG_INFO", out var infoLabel) == true)
        {
            fontId = infoLabel.Properties.GetValueOrDefault("FontType", fontId);
            infoColor = _confirmationColors.GetValueOrDefault(infoLabel.Properties.GetValueOrDefault("Color", ""), infoColor);
        }
        if (_groupDialogLabels?.TryResolve("OPTION_INFO_0", out var optionLabel) == true)
        {
            optionFontId = optionLabel.Properties.GetValueOrDefault("FontType", optionFontId);
            optionColor = _confirmationColors.GetValueOrDefault(optionLabel.Properties.GetValueOrDefault("Color", ""), optionColor);
        }
        if (Font(fontId) is not {} font || Font(optionFontId) is not {} optionFont) return;
        double fx = font.Scale * 640 / _size.Width, fy = font.Scale * 480 / _size.Height;
        double ox = optionFont.Scale * 640 / _size.Width, oy = optionFont.Scale * 480 / _size.Height;
        string cacheKey = prefix + "/Dialog";
        double limit = 320 / fx;
        if (!_wrappedText.TryGetValue(cacheKey, out var cached) || cached.Text != message || cached.Font != font || cached.Width != limit)
        {
            var lines = authoredLines ?? EnbRetailDialogText.Wrap(message, 320, c => (float)(font.CharacterAdvance(c) * fx));
            cached = (message, font, limit, lines.Take(small ? 16 : 10).ToArray());
            _wrappedText[cacheKey] = cached;
        }
        string[] options = Enumerable.Range(0, actions.Length).Select(i => definition?.GetValueOrDefault("Option" + i, "") ?? "").ToArray();
        EnbDialogBorder Border(string name)
        {
            var d = _frames[name].Definition;
            return new((float)d.BorderX, (float)d.BorderY);
        }
        var layout = EnbRetailDialogLayout.Measure(cached.Lines.Select(line => (float)(font.MeasureWidth(line) * fx)).ToArray(),
            (float)(font.CellHeight * fy), options.Select(text => (float)(optionFont.MeasureWidth(text) * ox)).ToArray(),
            (float)(optionFont.CellHeight * oy), Border("DIALOG_BOX_WINDOW"), Border("DIALOG_BOX_INFO"), Border("DIALOG_BOX_OPTION"));
        var placed=EnbRetailDialogLayout.Place(layout.Window.Width,layout.Window.Height,(float)(origin?.X??320),(float)(origin?.Y??240));
        double x=placed.X,y=placed.Y;
        Rect Place(EnbDialogRect r) => new(x + r.X, y + r.Y, r.Width, r.Height);
        Frame("DIALOG_BOX_WINDOW", Place(layout.Window), cacheKey);
        Frame("DIALOG_BOX_INFO", Place(layout.Info), prefix + "/Info");
        for (int i = 0; i < cached.Lines.Length; i++)
            Text(prefix + "/Message" + i, cached.Lines[i], Place(layout.Lines[i]), infoColor, fontId);
        for (int i = 0; i < actions.Length; i++)
        {
            Frame("DIALOG_BOX_OPTION", Place(layout.Options[i]), qualifyActions ? prefix + "/" + actions[i] : actions[i], enabled);
            Text(prefix + "/Option" + i, options[i], Place(layout.Labels[i]), optionColor, optionFontId);
        }
    }
}
