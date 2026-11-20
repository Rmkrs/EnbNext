using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Installed dynamic-window and dialog definitions, prepared by the asset session.</summary>
public sealed class EnbConfirmationResources
{
    public readonly record struct Frame(EnbAuthoredTooltipFrame Resize, EnbAuthoredUiAsset Asset);
    public Frame[] Frames { get; }
    public EnbRetailIniDocument Definitions { get; }
    public EnbInterfaceLayoutCatalog Labels { get; }

    internal EnbConfirmationResources(EnbRetailAssetBundle assets)
    {
        string directory = assets.Paths.ClientIniDirectory;
        var fonts = EnbInterfaceLayoutCatalog.Load(Path.Combine(directory, "fc_default.ini"));
        var windows = EnbRetailIniDocument.Load(Path.Combine(directory, "dynawin.ini"));
        var strings = EnbRenderSession.Of(assets).TooltipResources.Strings;
        Frames = new Frame[3];
        string[] names = ["DIALOG_BOX_WINDOW", "DIALOG_BOX_INFO", "DIALOG_BOX_OPTION"];
        for (int i = 0; i < names.Length; i++)
        {
            var definition = EnbRetailScannerTooltipDefinition.Parse(fonts, strings, windows, names[i]);
            var frame = definition is null ? null : EnbAuthoredTooltipFrame.Load(assets, definition);
            var asset = definition is null ? null : EnbAuthoredUiAsset.Load(assets, definition.MeshFile + ".w3d");
            if (frame is null || asset is null) throw new InvalidDataException("Authored dialog resource unavailable: " + names[i]);
            Frames[i] = new(frame, new(frame.Resource, asset.Hierarchy));
        }
        Definitions = EnbRetailIniDocument.Load(Path.Combine(directory, "dialogbox.ini"));
        Labels = EnbInterfaceLayoutCatalog.Load(Path.Combine(directory, "fc_dialogbox.ini"));
    }
}
