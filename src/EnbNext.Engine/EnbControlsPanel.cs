using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>005CB9D0 TSI31A visuals over the actual binding draft and capture owner.</summary>
internal sealed partial class EnbControlsPanel(EnbControlsResources resources, EnbOptionsPanelResources fonts,
    EnbFontEffectPalette palette, EnbConfirmationState confirmation) : IDisposable
{
    public EnbControlsResources Resources => resources;
    public EnbVerticalScrollbarVisual Scroll { get; } = new(resources.Scrollbar);
    public EnbSliderInput ScrollInput { get; } = new(resources.Track, resources.ScrollBounds);
    private EnbUiRect thumbPosition = resources.Track;
    private readonly object[] gadgets = Enumerable.Range(0, resources.Pieces.Length + 3).Select(_ => new object()).ToArray();
    private readonly EnbRetailText?[] labels = new EnbRetailText?[resources.Labels.Length + 1];
    internal EnbAttachedLabelAlpha?[] Alpha { get; } = new EnbAttachedLabelAlpha?[resources.Labels.Length];

    public void ResetOpening() { Array.Clear(Alpha); ResetNativeColors(); }
    internal EnbRetailText? Label(string attach)
        => labels[Array.FindIndex(resources.Labels, l => l.Attach == attach)];
    public EnbUiRect Design(string key) => key switch
    {
        "control/UP" => resources.Up,
        "control/DOWN" => resources.Down,
        "control/TRACK" => resources.Track,
        "control/THUMB" => thumbPosition,
        _ => resources.Pieces.First(p => p.Key == key).Bounds
    };
    // 00661700/005CA330 order includes keyboard and slider ahead of model buttons.
    public void PointerHits(Span<bool> hits, ReadOnlySpan<EnbRetailControlDefinition> rows, int offset,
        bool keyboardActive, bool sliderHit, double x, double y)
    {
        if (hits.Length != resources.PointerOrder.Length) throw new ArgumentException("One hit result is required per Controls gadget.", nameof(hits));
        hits.Clear(); hits[0] = keyboardActive; hits[1] = sliderHit;
        foreach (var piece in resources.Pieces)
        {
            if (piece.GadgetIndex < 0) continue;
            if (piece.Key.Contains("_KEY") && int.TryParse(piece.Key.AsSpan(piece.Key.Length - 2), out int row) &&
                (offset + row >= rows.Length || rows[offset + row].FixedKey is not null)) continue;
            hits[piece.GadgetIndex] = piece.Query.Contains(x, y);
        }
        foreach (var query in resources.BackgroundQueries)
            if (query.Contains(x, y)) { hits[^1] = true; break; }
    }

    public static bool IsButton(string key) => key.StartsWith("control/") &&
        (key.Contains("KEY") || key.Contains("BUTTON") || key is "control/EXIT" or "control/UP" or "control/DOWN" or "control/THUMB" or "control/TRACK" or "control/dialog/yes" or "control/dialog/no");

    public ImmutableList<EnbUiModelDraw> Draw(bool confirmation, EnbUiSize size, double scaling,
        string? hovered, string? pressed, long now, string? error)
        => DrawCore(confirmation, size, scaling, hovered, pressed, now, error, false);

    private ImmutableList<EnbUiModelDraw> DrawCore(bool confirmation, EnbUiSize size, double scaling,
        string? hovered, string? pressed, long now, string? error, bool native)
    {
        if (Draft is not { } draft) return [];
        var rows = Rows; var capture = Capture;
        ScrollInput.Configure(rows.Length, 11);
        int offset = ScrollInput.Offset;
        double height = ScrollInput.ThumbLength;
        Scroll.Resize(height);
        double travel = resources.Track.Height - height;
        thumbPosition = Scroll.ThumbBounds.Translate(resources.Track.Center.X,
            resources.Track.Y + (rows.Length > 11 ? travel * ScrollInput.Value / (rows.Length - 11) : 0));
        var draws = ImmutableList.CreateBuilder<EnbUiModelDraw>();
        var images = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        foreach (int i in resources.DrawOrder)
        {
            var piece = resources.Pieces[i];
            if (piece.Key.Contains("_KEY") && int.TryParse(piece.Key.AsSpan(piece.Key.Length - 2), out int row) &&
                rows.ElementAtOrDefault(offset + row) is not { FixedKey: null }) continue;
            DrawPiece(i, piece.Key, piece.Asset, piece.Bounds, piece.Bounds.Translate(-320, -240));
        }
        if (rows.Length > 11)
        {
            DrawPiece(resources.Pieces.Length, "control/UP", resources.Scrollbar.Up.Asset, resources.Up, resources.Scrollbar.Up.Bounds);
            DrawPiece(resources.Pieces.Length + 1, "control/DOWN", resources.Scrollbar.Down.Asset, resources.Down, resources.Scrollbar.Down.Bounds);
            DrawPiece(resources.Pieces.Length + 2, "control/THUMB", Scroll.Thumb, thumbPosition, Scroll.ThumbBounds);
        }
        for (int i = 0; i < resources.Labels.Length; i++)
        {
            var binding = resources.Labels[i]; var properties = binding.Definition.Properties;
            string attach = binding.Attach, text = properties.GetValueOrDefault("String0", "");
            bool alternate = false;
            if (attach.StartsWith("LABEL_TEXT") || attach.Contains("_KEY"))
            {
                int row = int.Parse(attach.AsSpan(attach.Length - 2));
                if (rows.ElementAtOrDefault(offset + row) is not { } definition) continue;
                if (attach.StartsWith("LABEL_TEXT")) text = definition.Text;
                else
                {
                    int slot = attach.StartsWith("PRIME") ? 1 : 2;
                    text = definition.FixedKey is { } fixedKey ? slot == 1 ? fixedKey : "" : draft.Binding(definition.Action, slot)?.Display ?? "";
                    if (capture.Action == definition.Action && capture.Slot == slot && !confirmation)
                    { text = native && Keyboard.Enabled ? Keyboard.Preview : "????"; alternate = true; }
                }
            }
            var alpha = Alpha[i] ??= new(); var effect = binding.Effect;
            float opacity = alpha.Update(now, native ? nativeLabelHover[i] : hovered == "control/" + attach, effect.Initial, effect.Target, effect.Fade, effect.Blink, effect.Revert);
            Text(i, text, binding.Anchor, native ? nativeInks[i] : palette.Color(properties[alternate ? "AltColor" : "Color"]),
                properties.GetValueOrDefault("FontType", "FONT_SMALL"), properties.GetValueOrDefault("Justify", "").EndsWith("RIGHT"), opacity);
        }
        if (error is not null) Text(labels.Length - 1, error, new(320, 395), new(1, .3f, .3f), "FONT_TINY", false, 1);
        if (draws.Count > 0) draws[^1] = draws[^1] with { Images = images.ToImmutable() };
        return draws.ToImmutable();

        EnbUiRect Screen(EnbUiRect r) => new(r.X * size.Width / 640, r.Y * size.Height / 480,
            r.Width * size.Width / 640, r.Height * size.Height / 480);
        void DrawPiece(int index, string key, EnbAuthoredUiAsset asset, EnbUiRect placed, EnbUiRect source)
        {
            if (!native)
            {
                draws.Add(new(asset, pressed == key && IsButton(key) ? 1u : 0u, Screen(placed), !confirmation && hovered == key && IsButton(key))
                { GadgetOwner = gadgets[index], DesignBounds = source });
                return;
            }
            bool slider = index >= resources.Pieces.Length;
            int gadget = slider ? SliderIndex : resources.Pieces[index].GadgetIndex < 0 ? -1 : resources.Pieces[index].GadgetIndex + DialogCount;
            var part = key switch { "control/UP" => EnbScrollbarPart.Up, "control/DOWN" => EnbScrollbarPart.Down,
                "control/THUMB" => EnbScrollbarPart.Thumb, _ => EnbScrollbarPart.None };
            bool selected = slider ? part is EnbScrollbarPart.Up or EnbScrollbarPart.Down && ScrollInput.Pressed == part
                : gadget >= 0 && Pointer.Selected(gadget);
            bool flash = gadget >= 0 && Pointer.Flash(gadget) && (!slider || Pointer.FlashSource(gadget) == (int)part);
            draws.Add(new(asset, 0, Screen(placed), flash)
            { GadgetOwner = gadgets[slider ? resources.Pieces.Length : index], DesignBounds = source,
              EmissiveOverride = selected ? new System.Numerics.Vector3(.35f) : null,
              FlashHighlight = flash && !Pointer.FlashInside(gadget) });
        }
        void Text(int index, string text, EnbUiPoint point, EnbRgb color, string fontId, bool right, float opacity)
        {
            if (fonts.Font(fontId, size, scaling) is not { } font) return;
            var label = labels[index] ??= new(); label.SetText(text, font, color); label.SetDisplayScale(1,1);
            if(index<resources.Labels.Length)label.FitTo(Screen(resources.Labels[index].Style.Clip).Size,scaling);
            if (label.Raster is not { } raster) return;
            images.Add(new(raster, new(point.X * size.Width / 640 - (right ? label.Width : label.Width / 2),
                point.Y * size.Height / 480 - label.Height / 2, label.Width, label.Height)) { Opacity = opacity });
        }
    }

    public void Dispose() { Retire(); if (dialogPanel is not null) dialogPanel.SoundRequested -= DialogSound; foreach (var label in labels) label?.Dispose(); }
}
