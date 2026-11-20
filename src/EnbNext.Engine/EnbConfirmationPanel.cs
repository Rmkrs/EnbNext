using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>00587D50/00588DC0 authored confirmation content and dynamic GPU frames.</summary>
internal sealed partial class EnbConfirmationPanel(EnbRetailAssetBundle assets, EnbFontEffectPalette palette) : IDisposable
{
    private readonly EnbConfirmationResources resources = EnbRenderSession.Of(assets).Confirmations;
    private readonly EnbOptionsPanelResources fonts = EnbRenderSession.Of(assets).OptionsPanels;
    private readonly EnbAuthoredUiAsset?[] resized = new EnbAuthoredUiAsset?[4];
    private readonly EnbUiSize[] sizes = new EnbUiSize[4];
    private readonly object?[] gadgets = new object?[4];
    private ulong lifetime;
    private readonly EnbRetailText?[] labels = new EnbRetailText?[18];
    private (string Text, string Definition, bool Notice, float? Wrap, EnbUiSize Size, double Scaling)? contentKey;
    private string[] lines = [], options = [];
    private EnbRetailDialogLayout? layout;
    public EnbUiRect[] Bounds { get; } = new EnbUiRect[4];
    public static string Key(int index) => index switch
    { 0 => "control/dialog/outer", 1 => "control/dialog/info", 2 => "control/dialog/yes", 3 => "control/dialog/no", _ => throw new ArgumentOutOfRangeException(nameof(index)) };

    private EnbRetailFontFace? preparedFont, preparedOptionFont;
    private EnbRgb infoColor, optionColor;

    public bool Prepare(EnbConfirmationState state, EnbUiSize size, double scaling)
    {
        Array.Clear(Bounds);
        if (state.Text is null) { RetireInput(); return false; }
        if (lifetime != state.Lifetime)
        {
            RetireInput();
            // 00588340 retires the previous dialog's children before 00587D50 creates the next.
            foreach (var label in labels) label?.Dispose();
            Array.Clear(labels);
            for (int i = 0; i < gadgets.Length; i++) gadgets[i] = new object();
            lifetime = state.Lifetime;
        }
        if (!resources.Definitions.TryGetSection(state.Definition, out var definition))
            throw new InvalidDataException("Authored dialog definition unavailable: " + state.Definition);
        bool small = definition.GetValueOrDefault("FontSize") == "FONT_SMALL";
        if (!resources.Labels.TryResolve(small ? "DIALOG_INFO_SMALL" : "DIALOG_INFO", out var infoLabel) ||
            !resources.Labels.TryResolve("OPTION_INFO_0", out var optionLabel))
            throw new InvalidDataException("Authored dialog text definition unavailable.");
        if (fonts.Font(infoLabel.Properties["FontType"], size, scaling) is not { } font ||
            fonts.Font(optionLabel.Properties["FontType"], size, scaling) is not { } optionFont) return false;
        double fx = font.Scale * 640 / size.Width, fy = font.Scale * 480 / size.Height;
        double ox = optionFont.Scale * 640 / size.Width, oy = optionFont.Scale * 480 / size.Height;
        var key = (state.Text, state.Definition, state.Notice, state.WrapWidth, size, scaling);
        if (contentKey != key)
        {
            lines = (state.WrapWidth is { } width
                ? EnbRetailDialogText.Wrap(state.Text, width, c => (float)(font.CharacterAdvance(c) * fx))
                : state.Text.Split('\n')).Take(small ? 16 : 10).ToArray();
            options = state.Notice ? [definition.GetValueOrDefault("Option0", "")]
                : [definition.GetValueOrDefault("Option0", ""), definition.GetValueOrDefault("Option1", "")];
            EnbDialogBorder Border(int index) => new((float)resources.Frames[index].Resize.Definition.BorderX,
                (float)resources.Frames[index].Resize.Definition.BorderY);
            layout = EnbRetailDialogLayout.Measure(lines.Select(line => (float)(font.MeasureWidth(line) * fx)).ToArray(),
                (float)(font.CellHeight * fy), options.Select(text => (float)(optionFont.MeasureWidth(text) * ox)).ToArray(),
                (float)(optionFont.CellHeight * oy), Border(0), Border(1), Border(2));
            contentKey = key;
        }
        preparedFont = font; preparedOptionFont = optionFont;
        infoColor = palette.Resolve(infoLabel.Properties, false, 0);
        optionColor = palette.Resolve(optionLabel.Properties, false, 0);
        var origin = EnbRetailDialogLayout.Place(layout!.Window.Width, layout.Window.Height, 320, 240);
        EnbUiRect Place(EnbDialogRect r) => new((double)origin.X + r.X, (double)origin.Y + r.Y, r.Width, r.Height);
        Bounds[0] = Place(layout.Window); Bounds[1] = Place(layout.Info);
        for (int i = 0; i < options.Length; i++) Bounds[i + 2] = Place(layout.Options[i]);
        PrepareKeys(definition);
        return true;
    }

    public ImmutableList<EnbUiModelDraw> Draw(EnbConfirmationState state, EnbUiSize size, double scaling,
        string? hovered, string? pressed) => Draw(state, size, scaling, hovered, pressed, false);

    public ImmutableList<EnbUiModelDraw> DrawFrame(EnbConfirmationState state, EnbUiSize size, double scaling)
        => Draw(state, size, scaling, null, null, true);

    private ImmutableList<EnbUiModelDraw> Draw(EnbConfirmationState state, EnbUiSize size, double scaling,
        string? hovered, string? pressed, bool native)
    {
        if (!Prepare(state, size, scaling)) return [];
        var font = preparedFont!; var optionFont = preparedOptionFont!;
        var placed = EnbRetailDialogLayout.Place(layout!.Window.Width, layout.Window.Height, 320, 240);
        // Promote the authored origin at the layout-to-render boundary, before translation.
        double originX = placed.X, originY = placed.Y;
        EnbUiRect Place(EnbDialogRect r) => new(originX + r.X, originY + r.Y, r.Width, r.Height);
        EnbUiRect Screen(EnbUiRect r) => new(r.X * size.Width / 640, r.Y * size.Height / 480,
            r.Width * size.Width / 640, r.Height * size.Height / 480);
        var draws = ImmutableList.CreateBuilder<EnbUiModelDraw>();
        var texts = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        Frame(0, 0, Place(layout.Window)); Frame(1, 1, Place(layout.Info));
        for (int i = 0; i < lines.Length; i++)
            Text(i, lines[i], Place(layout.Lines[i]), font, infoColor);
        for (int i = 0; i < options.Length; i++)
        {
            Frame(i + 2, 2, Place(layout.Options[i]));
            Text(16 + i, options[i], Place(layout.Labels[i]), optionFont, optionColor);
        }
        draws[^1] = draws[^1] with { Images = texts.ToImmutable() };
        return draws.ToImmutable();

        void Text(int index, string text, EnbUiRect rect, EnbRetailFontFace face, EnbRgb color)
        {
            var label = labels[index] ??= new();
            label.SetText(text, face, color); label.SetDisplayScale(1,1);
            if (label.Raster is not { } raster) return;
            // Center in physical pixels before converting to host coordinates. Dividing
            // the origin and width separately can move a half-pixel tie at fractional DPI.
            double physicalWidth = size.Width * scaling, physicalHeight = size.Height * scaling;
            double x = (rect.X + rect.Width / 2) * physicalWidth / 640 - label.Width * scaling / 2d;
            double y = (rect.Y + rect.Height / 2) * physicalHeight / 480 - label.Height * scaling / 2d;
            texts.Add(new(raster, new(x / scaling, y / scaling, label.Width, label.Height)));
        }
        void Frame(int index, int resource, EnbUiRect rect)
        {
            Bounds[index] = rect;
            if (resized[index] is null || sizes[index] != rect.Size)
            {
                var frame = resources.Frames[resource];
                resized[index] = frame.Asset with { Geometry = frame.Resize.Geometry((float)rect.Width, (float)rect.Height) };
                sizes[index] = rect.Size;
            }
            var asset = resized[index]!;
            var min = asset.Geometry!.Positions.Aggregate(Vector3.Min); var max = asset.Geometry.Positions.Aggregate(Vector3.Max);
            string key = Key(index);
            draws.Add(new(asset, !native && pressed == key ? 1u : 0u, Screen(rect), !native && index >= 2 && hovered == key)
            { EmissiveOverride = native && index != 1 ? new Vector3(InputEmissive(index)) : null,
              GadgetOwner = gadgets[index], DesignBounds = new(min.X, -max.Y, max.X - min.X, max.Y - min.Y) });
        }
    }

    public void Dispose() { DetachInput();RetireInput(); foreach (var label in labels) label?.Dispose(); }
}
