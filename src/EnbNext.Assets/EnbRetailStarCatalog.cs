using System.Globalization;

namespace EnbNext.Assets;

public enum EnbRetailStarLayerFamily
{
    None,
    ExactStarSet,
    HundredBucket,
    Default,
}

/// <summary>
/// One current-retail stars.ini layer. This retains the authored configuration
/// even though only NumStars and Quads participate in the proven StarBudget
/// accounting boundary.
/// </summary>
public sealed record EnbRetailStarLayerDefinition(
    int Ordinal,
    string SectionName,
    string? Texture,
    int TextureLog2Width,
    bool Quads,
    int NumStars,
    float MinSize,
    float MaxSize,
    int QmcOffset,
    bool PickRandomly,
    bool Hemisphere)
{
    public int RetailRenderCost => Quads
        ? unchecked(NumStars << 1)
        : NumStars;
}

public sealed record EnbRetailStarLayerSelection(
    EnbRetailStarLayerFamily Family,
    int RequestedStarSetId,
    int? SelectedStarSetId,
    IReadOnlyList<EnbRetailStarLayerDefinition> Layers)
{
    public int TotalConfiguredRenderCost
    {
        get
        {
            int total = 0;
            foreach (EnbRetailStarLayerDefinition layer in Layers)
                total = unchecked(total + layer.RetailRenderCost);
            return total;
        }
    }
}

/// <summary>
/// Data-driven reader for the retail Data/client/ini/stars.ini section family.
/// Current retail tries STAR&lt;exact-id&gt;\0..N first, then the signed id truncated
/// to its 100-bucket, then Default\0..N. Each family stops at its first missing
/// ordinal and the first non-empty family wins.
/// </summary>
public sealed class EnbRetailStarCatalog
{
    private readonly EnbRetailIniDocument _document;

    private EnbRetailStarCatalog(EnbRetailIniDocument document)
    {
        _document = document;
    }

    public static EnbRetailStarCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new EnbRetailStarCatalog(EnbRetailIniDocument.Load(path));
    }

    public static EnbRetailStarCatalog ParseText(string text)
        => new(EnbRetailIniDocument.Parse(text));

    public EnbRetailStarLayerSelection ResolveLayers(int starSetId)
    {
        IReadOnlyList<EnbRetailStarLayerDefinition> exact = ReadStarFamily(starSetId);
        if (exact.Count != 0)
        {
            return new EnbRetailStarLayerSelection(
                EnbRetailStarLayerFamily.ExactStarSet,
                starSetId,
                starSetId,
                exact);
        }

        int bucket = unchecked((starSetId / 100) * 100);
        IReadOnlyList<EnbRetailStarLayerDefinition> bucketLayers = ReadStarFamily(bucket);
        if (bucketLayers.Count != 0)
        {
            return new EnbRetailStarLayerSelection(
                EnbRetailStarLayerFamily.HundredBucket,
                starSetId,
                bucket,
                bucketLayers);
        }

        IReadOnlyList<EnbRetailStarLayerDefinition> defaults = ReadFamily(
            ordinal => $"Default\\{ordinal}");
        return new EnbRetailStarLayerSelection(
            defaults.Count == 0 ? EnbRetailStarLayerFamily.None : EnbRetailStarLayerFamily.Default,
            starSetId,
            SelectedStarSetId: null,
            defaults);
    }

    private IReadOnlyList<EnbRetailStarLayerDefinition> ReadStarFamily(int starSetId)
        => ReadFamily(ordinal => $"STAR{starSetId.ToString("D4", CultureInfo.InvariantCulture)}\\{ordinal}");

    private IReadOnlyList<EnbRetailStarLayerDefinition> ReadFamily(Func<int, string> sectionNameFactory)
    {
        var layers = new List<EnbRetailStarLayerDefinition>();
        for (int ordinal = 0; ; ordinal++)
        {
            string sectionName = sectionNameFactory(ordinal);
            if (!_document.TryGetSection(sectionName, out IReadOnlyDictionary<string, string> values))
                break;

            layers.Add(new EnbRetailStarLayerDefinition(
                ordinal,
                sectionName,
                ReadString(values, "Texture"),
                ReadInt(values, "TextureLog2Width", 0),
                ReadBool(values, "Quads", false),
                ReadInt(values, "NumStars", 1000),
                ReadFloat(values, "MinSize", 0f),
                ReadFloat(values, "MaxSize", 1f),
                ReadInt(values, "QMCOffset", 0),
                ReadBool(values, "PickRandomly", true),
                ReadBool(values, "Hemisphere", false)));
        }

        return layers;
    }

    private static string? ReadString(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out string? value) ? value : null;

    private static int ReadInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        int defaultValue)
        => EnbRetailIniDocument.TryReadInt(values, key, out int value)
            ? value
            : defaultValue;

    private static float ReadFloat(
        IReadOnlyDictionary<string, string> values,
        string key,
        float defaultValue)
        => EnbRetailIniDocument.TryReadFloat(values, key, out float value) && float.IsFinite(value)
            ? value
            : defaultValue;

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> values,
        string key,
        bool defaultValue)
        => values.ContainsKey(key)
            ? EnbRetailIniDocument.ReadBool(values, key)
            : defaultValue;
}
