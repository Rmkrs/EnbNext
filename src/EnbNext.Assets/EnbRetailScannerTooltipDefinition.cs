namespace EnbNext.Assets;

/// <summary>Installed tooltip.ini, fc_default.ini and dynawin.ini scanner presentation.</summary>
public sealed record EnbRetailScannerTooltipDefinition(
    string FontId, string PreviousText, string NextText,
    float BorderX, float BorderY, float MinWidth, float MinHeight)
{
    public string HullText { get; init; } = "";
    public string ShieldText { get; init; } = "";
    public string NoHullText { get; init; } = "";
    public string NoShieldText { get; init; } = "";
    public string VerbText { get; init; } = "";
    public string InterruptProgressText {get;init;} = "";
    public string ActivationProgressText {get;init;} = "";
    public string MeshFile { get; init; } = "";
    public string AnimationX { get; init; } = "";
    public string AnimationY { get; init; } = "";
    public float MaxWidth { get; init; }
    public float MaxHeight { get; init; }

    public string FormatGauge(bool hull, float? current, float? maximum)
    {
        float value=current??0, max=maximum??0;
        if (!float.IsFinite(value) || !float.IsFinite(max) || (!hull && max <= 0))
            return hull ? NoHullText : NoShieldText;
        // 5DC8F5..5DC902 zero both hull locals below capacity1. Otherwise
        // 5DC4A0 clamps current to capacity before integer formatting.
        if(hull && max<1){value=0;max=0;}
        else value=Math.Clamp(value,0,max);
        string text = hull ? HullText : ShieldText;
        foreach (float number in new[] { value,max })
        {
            int index = text.IndexOf("%d",StringComparison.Ordinal);
            if (index < 0) break;
            text = text[..index] + ((int)number).ToString(System.Globalization.CultureInfo.InvariantCulture) + text[(index+2)..];
        }
        return text;
    }
    public static EnbRetailScannerTooltipDefinition? Load(string iniDirectory)
    {
        try
        {
            return Parse(EnbInterfaceLayoutCatalog.Load(Path.Combine(iniDirectory, "fc_default.ini")),
                EnbRetailIniDocument.Load(Path.Combine(iniDirectory, "tooltip.ini")),
                EnbRetailIniDocument.Load(Path.Combine(iniDirectory, "dynawin.ini")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static EnbRetailScannerTooltipDefinition? Parse(EnbInterfaceLayoutCatalog fonts,
        EnbRetailIniDocument text, EnbRetailIniDocument windows, string windowSection = "TOOLTIPS", string? fontOverride = null)
    {
        if (!fonts.TryResolve("TOOLTIP_FONT", out var font) ||
            !font.Properties.TryGetValue("FontType", out string? fontId) ||
            !text.TryGetSection("UI_SCAN", out var strings) ||
            !strings.TryGetValue("PREV_TARGET", out string? previous) ||
            !strings.TryGetValue("NEXT_TARGET", out string? next)) return null;
        var values = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string section = windowSection;
        while (visited.Add(section) && windows.TryGetSection(section, out var entries))
        {
            foreach (var entry in entries) values.TryAdd(entry.Key, entry.Value);
            if (!entries.TryGetValue("Use", out string? parent))
            {
                if (EnbRetailIniDocument.TryReadFloat(values,"BorderX",out float x) &&
                    EnbRetailIniDocument.TryReadFloat(values,"BorderY",out float y) &&
                    EnbRetailIniDocument.TryReadFloat(values,"MinX",out float minX) &&
                    EnbRetailIniDocument.TryReadFloat(values,"MinY",out float minY) &&
                    new[] { x,y,minX,minY }.All(v => float.IsFinite(v) && v >= 0))
                    return new(fontOverride ?? fontId,previous.Replace("\\n","\n"),next.Replace("\\n","\n"),x,y,minX,minY)
                    {
                        HullText = PowerText(text,"HULL"), ShieldText = PowerText(text,"SHIELDS"),
                        NoHullText = PowerText(text,"NO_HULL"), NoShieldText = PowerText(text,"NO_SHIELDS"),
                        VerbText = text.TryGetSection("UI_VERB",out var verbs) ? verbs.GetValueOrDefault("STRING", "") : "",
                        InterruptProgressText = text.TryGetSection("UI_INTERRUPT",out var interrupt) ? interrupt.GetValueOrDefault("INT_PROGRESS","").Replace("\\n","\n") : "",
                        ActivationProgressText = text.TryGetSection("UI_INTERRUPT",out var activation) ? activation.GetValueOrDefault("SKILL_PROGRESS","").Replace("\\n","\n") : "",
                        MeshFile = values.GetValueOrDefault("MeshFile", ""),
                        AnimationX = values.GetValueOrDefault("AnimX", ""), AnimationY = values.GetValueOrDefault("AnimY", ""),
                        MaxWidth = EnbRetailIniDocument.TryReadFloat(values,"MaxX",out float maxX) ? maxX : minX,
                        MaxHeight = EnbRetailIniDocument.TryReadFloat(values,"MaxY",out float maxY) ? maxY : minY,
                    };
                return null;
            }
            section = parent;
        }
        return null;
    }

    private static string PowerText(EnbRetailIniDocument document, string key)
        => document.TryGetSection("UI_POWER",out var section) && section.TryGetValue(key,out var value)
            ? value.Replace("\\n","\n") : "";
}
