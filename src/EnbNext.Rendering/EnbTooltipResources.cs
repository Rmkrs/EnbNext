using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Installed tooltip data belonging to one retail asset session.</summary>
public sealed class EnbTooltipResources
{
    public EnbRetailIniDocument Strings { get; }
    public EnbRetailScannerTooltipDefinition? Definition { get; }
    public EnbAuthoredTooltipFrame? Frame { get; }

    internal EnbTooltipResources(EnbRetailAssetBundle assets)
    {
        Strings = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "tooltip.ini"));
        Definition = EnbRetailScannerTooltipDefinition.Load(assets.Paths.ClientIniDirectory);
        Frame = Definition is { } definition ? EnbAuthoredTooltipFrame.Load(assets, definition) : null;
    }
}
