using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using Rect = EnbNext.Presentation.EnbUiRect;

namespace EnbNext.Engine;

/// <summary>
/// Next Modern UI item details (a right-click on an item): the retail examination window's content (EnbItemInformation:
/// name, level and type, attributes, specials, description, Auto Fire) in a compact one-column Modern UI window laid
/// out like the item's tooltip card. It keeps the retail window's place, drag, close, tooltips and Auto Fire request;
/// a long description scrolls.
/// </summary>
internal sealed partial class EnbInventoryUi
{
    private int _mdetailScroll;
    private EnbInventoryItemRequest? _mdetailFor;
    private string _mdetailName = "";

    private void ModernDetails(EnbSceneItemSnapshot item)
    {
        Flush();
        if (_mdetailFor != _examined) { _mdetailFor = _examined; _mdetailScroll = 0; }
        var attributes = EnbItemInformation.Attributes(item);
        var specials = EnbItemInformation.Specials(item, Data?.RequirementState);
        double u = ModernUnit, text = 14 * u, small = 13 * u, title = 16 * u, pad = Math.Round(11 * u), gap = Math.Round(6 * u), head = Math.Round(20 * u);
        double row = Math.Round(18 * u), line = Math.Round(17 * u), iconSize = Math.Round(42 * u), close = Math.Round(22 * u);
        double width = Math.Round(400 * u), inner = width - pad * 2;
        const int DescriptionLines = 6;

        // One column, as the tooltip card: header, attributes, specials, description; measured first so the window fits.
        var specialLines = specials.Select(s => ModernWrap(s.Text, inner, small)).ToList();
        int specialCount = specialLines.Sum(l => Math.Max(1, l.Count));
        var description = ModernWrap(item.Description ?? "", inner - ModernScrollbarRoom, small);
        int shown = Math.Min(DescriptionLines, description.Count);
        double header = iconSize;
        double attributesHeight = attributes.Length > 0 ? gap + head + attributes.Length * row : 0;
        double specialsHeight = specials.Length > 0 ? gap + head + specialCount * line : 0;
        double descriptionHeight = shown > 0 ? gap + head + shown * line : 0;
        double height = pad + header + attributesHeight + specialsHeight + descriptionHeight + (item.HasAutoFire ? gap + head : 0) + (_message is null ? 0 : gap + line) + pad;

        double x = _detailOrigin.X * _size.Width / 640, y = _detailOrigin.Y * _size.Height / 480;
        x = Snap(Math.Clamp(x, 0, Math.Max(0, _size.Width - width))); y = Snap(Math.Clamp(y, 0, Math.Max(0, _size.Height - height)));
        var frame = new Rect(x, y, width, height);
        ModernFrame("DIALOG_BOX_WINDOW", frame, "detailFrame");
        Flush();
        // Wiki: the item's Net-7 Wiki page (drops, build recipes) in the wiki window, left of the close button.
        _mdetailName = item.Name ?? "";
        double wikiWidth = _mdetailName.Trim().Length > 0 ? Math.Ceiling(ModernMeasure("Wiki", small) + 18 * u) : 0;
        // The header drags the window (the retail detailDrag), the close button closes it.
        Hit("detailDrag", FromPixels(new(x, y, width - pad - close - (wikiWidth > 0 ? wikiWidth + gap : 0), pad + header)));
        ModernCloseButton("detailClose", new(x + width - pad - close, y + pad, close, close));

        // Icon in its category frame, name, level and type.
        var iconArea = new Rect(x + pad, y + pad, iconSize, iconSize);
        if (item.ItemType != uint.MaxValue && Texture("icon_" + SlotColor(item) + ".tga") is { } slot) _images.Add(new(slot, iconArea));
        if (Icon(item.IconBaseAsset) is { } icon) _images.Add(new(icon, new(iconArea.X + 2 * u, iconArea.Y + 3 * u, iconArea.Width - 4 * u, iconArea.Height - 6 * u)));
        if (wikiWidth > 0) ModernButton("mwiki/item", "Wiki", new(x + width - pad - close - gap - wikiWidth, y + pad, wikiWidth, close), small, "Look this item up on the Net-7 Wiki.");
        double tx = iconArea.Right + gap + 2 * u, tw = x + width - pad - close - gap - (wikiWidth > 0 ? wikiWidth + gap : 0) - tx;
        ModernText("mdetail/name", ModernEllipsis(_mdetailName, tw, title), new(tx, y + pad, tw, iconSize / 2), new(1, 1, 1), title);
        ModernText("mdetail/level", ModernEllipsis($"Level {item.Level} {EnbItemInformation.TypeName(item.Subcategory)}", tw, small),
            new(tx, y + pad + iconSize / 2, tw, iconSize / 2), new(.55f, 1, .55f), small);

        var heading = new EnbRgb(.7f, .8f, 1);
        double cy = y + pad + header;
        void Rule(double at) => _images.Add(new(EnbModernIcons.Fill(new(.45f, .55f, .85f), 70), new(x + pad, Snap(at), inner, Math.Max(1, Math.Round(_scale)) / _scale)));

        // Attributes: name and value; an attribute with an explanation shows it as its tooltip.
        if (attributes.Length > 0)
        {
            Rule(cy + gap / 2); cy += gap;
            ModernText("mdetail/attributes", "Attributes", new(x + pad, cy, inner, head), heading, small);
            cy += head;
            for (int i = 0; i < attributes.Length; i++)
            {
                var (name, value) = attributes[i];
                string id = "attribute/" + i.ToString(CultureInfo.InvariantCulture);
                var r = new Rect(x + pad - 3 * u, cy + i * row, inner + 6 * u, row);
                string help = EnbItemInformation.AttributeHelpAt(item, i);
                if (help.Length > 0) { Hit(id, FromPixels(r)); _tips[id] = help; }
                bool hot = _hover == id && help.Length > 0;
                if (hot) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(r.Width * _scale), (int)Math.Round(r.Height * _scale)), r));
                double half = inner * .58;
                ModernText(id + "/name", ModernEllipsis(name, half - 4 * u, small), new(x + pad, r.Y, half, row), hot ? new(1, 1, .45f) : new(.76f, .8f, .9f), small);
                ModernText(id + "/value", ModernEllipsis(value, inner - half, small), new(x + pad + half, r.Y, inner - half, row), new(1, 1, 1), small, 2);
            }
            cy += attributes.Length * row;
        }

        // Specials (requirements and restrictions), each with its explanation as tooltip.
        if (specials.Length > 0)
        {
            Rule(cy + gap / 2); cy += gap;
            ModernText("mdetail/specials", "Specials", new(x + pad, cy, inner, head), heading, small);
            cy += head;
            for (int i = 0; i < specials.Length; i++)
            {
                string id = "special/" + i.ToString(CultureInfo.InvariantCulture);
                var lines = specialLines[i];
                var r = new Rect(x + pad - 3 * u, cy, inner + 6 * u, Math.Max(1, lines.Count) * line);
                if (specials[i].Tooltip.Length > 0) { Hit(id, FromPixels(r)); _tips[id] = specials[i].Tooltip; }
                bool hot = _hover == id && specials[i].Tooltip.Length > 0;
                if (hot) _images.Add(new(EnbModernIcons.SlotHighlight((int)Math.Round(r.Width * _scale), (int)Math.Round(r.Height * _scale)), r));
                for (int n = 0; n < lines.Count; n++)
                    ModernText(id + "/" + n.ToString(CultureInfo.InvariantCulture), lines[n], new(x + pad, cy + n * line, inner, line), hot ? new(1, 1, .45f) : specials[i].Unmet ? new(1, .45f, .45f) : new(1, .78f, .45f), small);
                cy += r.Height;
            }
        }

        // The description; a long one scrolls.
        if (shown > 0)
        {
            Rule(cy + gap / 2); cy += gap;
            ModernText("mdetail/descriptionTitle", "Description", new(x + pad, cy, inner, head), heading, small);
            cy += head;
            var area = new Rect(x + pad, cy, inner, shown * line);
            Hit("mdetail/description", FromPixels(area));
            _mdetailScroll = Math.Clamp(_mdetailScroll, 0, Math.Max(0, description.Count - DescriptionLines));
            for (int n = 0; n < shown && _mdetailScroll + n < description.Count; n++)
                ModernText("mdetail/d/" + n.ToString(CultureInfo.InvariantCulture), description[_mdetailScroll + n],
                    new(area.X, area.Y + n * line, area.Width - ModernScrollbarRoom, line), new(.9f, .93f, 1), small);
            ModernScrollbar("mdetail/description", area, description.Count, DescriptionLines, _mdetailScroll, v => _mdetailScroll = v);
            cy += area.Height;
        }

        // Auto Fire, as the retail window's option; a reason a request failed below.
        double by = cy + gap;
        if (item.HasAutoFire)
        {
            bool enabled = !_busy && item.State is not null, hot = enabled && _hover == "auto";
            int box = (int)Math.Round(16 * u * _scale);
            var check = new Rect(x + pad, Snap(by + head / 2 - box / _scale / 2), box / _scale, box / _scale);
            _images.Add(new(EnbModernIcons.Check(box, ShownAutoFire(item), hot), check) { Opacity = enabled ? 1 : .5f });
            double labelWidth = ModernMeasure("Auto Fire", text) + 12 * u;
            if (enabled) Hit("auto", FromPixels(new(check.X - 3 * u, by, check.Width + 10 * u + labelWidth, head)));
            ModernText("mdetail/auto", "Auto Fire", new(check.Right + 10 * u, by, labelWidth, head), hot ? new(1, 1, .45f) : new(.92f, .94f, 1), text);
            _tips["auto"] = "When selected, weapon will fire automatically.";
            by += head + gap;
        }
        if (_message is not null)
            ModernText("mdetail/message", ModernEllipsis(_message, inner, small), new(x + pad, by, inner, line), new(1, .5f, .5f), small);
        Flush();
    }

    /// <summary>The wheel over the item details' description scrolls it.</summary>
    private bool ScrollModernDetails(string id, double delta)
    {
        if (!ModernUi || _examined is null || delta == 0 || !id.StartsWith("mdetail/description", StringComparison.Ordinal)) return false;
        _mdetailScroll = Math.Max(0, _mdetailScroll + (delta < 0 ? 1 : -1));
        Publish(); return true;
    }
}
