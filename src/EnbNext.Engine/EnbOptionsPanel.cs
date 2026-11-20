using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>Retained authored Main/Camera labels and GPU draws, independent of the host.</summary>
internal sealed class EnbOptionsPanel(EnbOptionsPanelResources resources, EnbOptionsPanelResources.Panel panel,
    EnbFontEffectPalette palette) : IDisposable
{
    private readonly EnbRetailText?[] labels = new EnbRetailText?[panel.LabelIds.Length + panel.NextLabels.Length];
    private readonly object[] gadgetOwners = panel.Pieces.Select(_ => new object()).ToArray();
    private EnbRetailText? errorLabel;
    private readonly EnbAttachedFontColor[] clocks = new EnbAttachedFontColor[panel.LabelIds.Length + panel.NextLabels.Length];
    private readonly EnbRgb[] inks = panel.LabelBindings.Concat(panel.NextLabels.Select(n => panel.LabelBindings[n.SourceLabel]))
        .Select(b => b.Definition is { } d ? palette.ResolveSelected(d.Properties, false, false, 0) : default).ToArray();
    public EnbUiGadgetPointer Pointer { get; } = new(panel.PointerOrder.Length, ReferenceEquals(panel, resources.Camera) ? 0x7fu : 0);
    public string? Key(int index) => index < 0 ? null : index < panel.PointerOrder.Length ? panel.PointerOrder[index]
        : index == panel.PointerOrder.Length ? (ReferenceEquals(panel, resources.Camera) ? "camera/FRAME" : "main/FRAME") : null;

    public uint PointerHits(double x, double y)
    {
        uint mask = 0;
        for (int i = 0; i < panel.PointerOrder.Length; i++)
            foreach (var piece in panel.Pieces)
                if (piece.Key == panel.PointerOrder[i] && piece.Query.Contains(x, y)) mask |= 1u << i;
        foreach (var piece in panel.Pieces)
            if ((piece.Query with { ParentSphere = panel.BackgroundSphere }).Contains(x, y) ||
                piece.Source is not null && piece.Key.EndsWith("/FRAME") && piece.Bounds.Contains(new(x * 640, y * 480)))
            { mask |= 1u << panel.PointerOrder.Length; break; }
        return mask;
    }

    public string? AdvanceInput(EnbUiInputEvent input, bool left, bool right, float x, float y,
        int width, int height, long milliseconds, EnbCameraOptionsState camera)
    {
        PrepareInput(input);
        var frame = new EnbUiPointerFrame(input.Code, input.Modifiers, left, right);
        Pointer.DispatchPrepared(ref frame);
        return CompleteInput(frame, x, y, width, height, milliseconds, camera);
    }

    public void PrepareInput(EnbUiInputEvent input)
        => Pointer.Prepare(PointerHits(Math.Clamp(input.X, 0, 1), Math.Clamp(input.Y, 0, 1)));

    public string? CompleteInput(EnbUiPointerFrame frame, float x, float y, int width, int height,
        long milliseconds, EnbCameraOptionsState camera)
    {
        int action = Pointer.Command(frame);
        if (Pointer.ToggleHandler >= 0) camera.Toggle(int.Parse(panel.PointerOrder[Pointer.ToggleHandler].AsSpan(12)));
        for (int i = 0; i < panel.LabelBindings.Length; i++)
        {
            var binding = panel.LabelBindings[i];
            if (binding.Style is { } style && binding.Definition is { } d)
                inks[i] = clocks[i].Update(palette, d.Properties, EnbAttachedFontColor.Contains(i < panel.LabelShifts.Length ? style.Clip.Translate(panel.LabelShifts[i].X, panel.LabelShifts[i].Y) : style.Clip, width, height, x, y),
                    binding.GadgetIndex >= 0 && Pointer.Selected(binding.GadgetIndex), milliseconds);
        }
        for (int i = 0; i < panel.NextLabels.Length; i++)
        {
            var next = panel.NextLabels[i]; var binding = panel.LabelBindings[next.SourceLabel]; int slot = panel.LabelIds.Length + i;
            if (binding.Style is { } style && binding.Definition is { } d)
                inks[slot] = clocks[slot].Update(palette, d.Properties, EnbAttachedFontColor.Contains(style.Clip.Translate(next.Shift.X, next.Shift.Y), width, height, x, y),
                    Pointer.Selected(next.GadgetIndex), milliseconds);
        }
        return Key(action);
    }

    public void RetireInput()
    {
        Pointer.Retire(); Array.Clear(clocks);
        for (int i = 0; i < inks.Length; i++)
            if (panel.LabelBindings[i < panel.LabelIds.Length ? i : panel.NextLabels[i - panel.LabelIds.Length].SourceLabel].Definition is { } d)
                inks[i] = palette.ResolveSelected(d.Properties, false, false, 0);
    }

    public EnbUiRect Design(string key) => panel.Pieces.First(p => p.Key == key).Bounds;
    internal EnbRetailText? Label(string id) => labels[Array.IndexOf(panel.LabelIds, id)];

    // Historical fixed-state oracle; production uses DrawFrame.
    public ImmutableList<EnbUiModelDraw> DrawDiagnostic(EnbUiSize size, double scaling, EnbCameraOptionsState camera,
        string? hovered, string? pressed, long hoverElapsed, string? error)
        => DrawCore(size, scaling, camera, hovered, pressed, hoverElapsed, error, false);

    public ImmutableList<EnbUiModelDraw> DrawFrame(EnbUiSize size, double scaling, EnbCameraOptionsState camera, string? error = null)
        => DrawCore(size, scaling, camera, null, null, 0, error, true);

    private ImmutableList<EnbUiModelDraw> DrawCore(EnbUiSize size, double scaling, EnbCameraOptionsState camera,
        string? hovered, string? pressed, long hoverElapsed, string? error, bool native)
    {
        var draws = ImmutableList.CreateBuilder<EnbUiModelDraw>();
        for (int i = 0; i < panel.Pieces.Length; i++)
        {
            var piece = panel.Pieces[i];
            string key = piece.Key;
            bool button = key is "main/EXIT" or "camera/EXIT" or "camera/OK" or "camera/CANCEL" || key.StartsWith("main/BTTN") || key.StartsWith("main/NEXT") || key.StartsWith("camera/CHECK");
            int index = piece.GadgetIndex;
            bool selected = native ? index >= 0 && Pointer.Selected(index) : button && pressed == key;
            var asset = piece.Checked is { } on && camera[int.Parse(key.AsSpan(12))] ? on : piece.Asset;
            var r = piece.Bounds;
            draws.Add(new(asset, !native && selected ? 1u : 0u,
                new(r.X * size.Width / 640, r.Y * size.Height / 480, r.Width * size.Width / 640, r.Height * size.Height / 480),
                native ? index >= 0 && Pointer.Flash(index) : button && hovered == key && !selected)
                { GadgetOwner = gadgetOwners[i], DesignBounds = (piece.Source ?? r).Translate(-320, -240),
                  EmissiveOverride = native && selected ? new System.Numerics.Vector3(.35f) : null,
                  FlashHighlight = native && index >= 0 && Pointer.Flash(index) && !Pointer.FlashInside(index) });
        }
        var images = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        string prefix = panel == resources.Camera ? "camera/" : "main/";
        for (int slot = 0; slot < labels.Length; slot++)
        {
            int i = slot < panel.LabelIds.Length ? slot : panel.NextLabels[slot - panel.LabelIds.Length].SourceLabel;
            var shift = slot < panel.LabelIds.Length ? (slot < panel.LabelShifts.Length ? panel.LabelShifts[slot] : default) : panel.NextLabels[slot - panel.LabelIds.Length].Shift;
            if (i < 0 || !panel.Layout.TryResolve(panel.LabelIds[i], out var d) || resources.Font(d.Properties.GetValueOrDefault("FontType", ""), size, scaling) is not { } font) continue;
            var pivot = panel.Asset.Hierarchy.Pivots.First(p => p.Name.Equals(d.Properties["Attach"], StringComparison.OrdinalIgnoreCase));
            var point = EnbAuthoredUiAsset.ResolveTransforms(panel.Asset)[pivot.Ordinal].Translation + new System.Numerics.Vector3((float)shift.X, -(float)shift.Y, 0);
            var label = labels[slot] ??= new();
            string hoverKey = slot < panel.LabelIds.Length ? prefix + pivot.Name.ToUpperInvariant() : $"main/NEXT{slot - panel.LabelIds.Length:00}";
            label.SetText(slot < panel.LabelIds.Length ? d.Properties.GetValueOrDefault("String0", "") : panel.NextLabels[slot - panel.LabelIds.Length].Text, font,
                native ? inks[slot] : palette.Resolve(d.Properties, hovered == hoverKey, hoverElapsed));
            label.SetDisplayScale(1,1);
            if(panel.LabelBindings[i].Style is {} style)
                label.FitTo(new(style.Clip.Width*size.Width/640,style.Clip.Height*size.Height/480),scaling);
            if (label.Raster is not { } raster) continue;
            string justify = d.Properties.GetValueOrDefault("Justify", "");
            double offset = justify.EndsWith("LEFT", StringComparison.OrdinalIgnoreCase) ? 0
                : justify.EndsWith("RIGHT", StringComparison.OrdinalIgnoreCase) ? label.Width : label.Width / 2;
            images.Add(new(raster, new((point.X + 320) * size.Width / 640 - offset,
                (240 - point.Y) * size.Height / 480 - label.Height / 2, label.Width, label.Height)));
        }
        if (error is not null && resources.Font("FONT_TINY", size, scaling) is { } errorFont)
        {
            var label = errorLabel ??= new();
            label.SetText(error, errorFont, new(1, .3f, .3f)); label.SetDisplayScale(1,1);
            if (label.Raster is { } raster) images.Add(new(raster, new((size.Width - label.Width) / 2, size.Height * .47, label.Width, label.Height)));
        }
        if (draws.Count > 0) draws[^1] = draws[^1] with { Images = images.ToImmutable() };
        return draws.ToImmutable();
    }

    public void Dispose() { foreach (var label in labels) label?.Dispose(); errorLabel?.Dispose(); }
}
