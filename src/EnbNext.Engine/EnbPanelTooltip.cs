using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>006AB5B0/006AB930 target, capture and Alt lifetime for an authored panel.</summary>
internal sealed partial class EnbPanelTooltip(EnbRetailAssetBundle assets,
    Func<string, EnbUiSize, double, EnbRetailFontFace?> resolveFont) : IDisposable
{
    private readonly EnbTooltipResources resources = EnbRenderSession.Of(assets).TooltipResources;
    private readonly EnbCurrentRetailTooltipDelay delay = new();
    private readonly EnbRetailText?[] labels = new EnbRetailText?[20];
    private Func<int, EnbRetailText>? label;
    private EnbUiGadgetPointer? targetOwner;
    public bool Immediate { get => delay.Immediate; set => delay.Immediate = value; }
    public void Reset() { delay.Reset(); targetOwner = null; }

    public EnbUiTooltipDraw? Draw(EnbUiGadgetPointer pointer, string? key, string section,
        EnbUiInputQueue input, EnbUiSize size, double scaling, long milliseconds, uint? character, string? messageOverride = null, int? gadgetIndex = null)
    {
        int gadget = gadgetIndex ?? pointer.TooltipGadget;
        if (targetOwner != pointer) { delay.Reset(); targetOwner = pointer; }
        if (key is null) { Reset(); return null; }
        string message = messageOverride ?? (resources.Strings.TryGetSection(section, out var strings)
            ? strings.GetValueOrDefault(key[(key.IndexOf('/') + 1)..], "") : "");
        if (message.Length == 0) { Reset(); return null; }
        if (resources.Definition is not { } definition || resources.Frame is not { } frame ||
            resolveFont(definition.FontId, size, scaling) is not { } font) return null;
        bool captured = pointer.LeftCapture == gadget || pointer.RightCapture == gadget;
        if (!delay.IsReady(gadget, milliseconds,
            EnbCurrentRetailTooltipDelay.Milliseconds(assets.ResolvePlayerOptions(character).Options.InterfaceTooltipDelay), captured)) return null;
        var point = new EnbUiPoint(input.X * (Math.Max(1, Math.Round(size.Width * scaling)) - 1) / scaling,
            input.Y * (Math.Max(1, Math.Round(size.Height * scaling)) - 1) / scaling);
        return EnbTooltipContent.Create(frame, font, message, size, scaling, new(.1f, 1, .1f), point,
            label ??= GetLabel);
    }

    private EnbRetailText GetLabel(int index) => labels[index] ??= new();
    public void Dispose() { Reset(); foreach (var text in labels) text?.Dispose(); }
}
