using System.Collections.Immutable;

namespace EnbNext.Assets;

public sealed record EnbRetailBuffDefinition(string Type, string Asset, string Name, string Description, bool IsGood);

/// <summary>Current retail BuffDefinitionDatabase (00676DB0); no inferred icons or labels.</summary>
public sealed class EnbRetailBuffCatalog
{
    public ImmutableDictionary<string, EnbRetailBuffDefinition> Definitions { get; }

    private EnbRetailBuffCatalog(ImmutableDictionary<string, EnbRetailBuffDefinition> definitions) => Definitions = definitions;

    public static EnbRetailBuffCatalog Load(string iniDirectory)
        => Parse(EnbRetailIniDocument.Load(Path.Combine(iniDirectory, "buffdef.ini")));

    public static EnbRetailBuffCatalog Parse(EnbRetailIniDocument ini)
    {
        var result = ImmutableDictionary.CreateBuilder<string, EnbRetailBuffDefinition>(StringComparer.Ordinal);
        for (int i = 0; ini.TryGetSection($"BUFF-{i}", out var values); i++)
        {
            string Read(string key) => values.GetValueOrDefault(key, "").Replace("\\n", "\n");
            string type = Read("BuffType"), asset = Read("BuffAsset");
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(asset)) continue;
            // 00676DB0 logs and discards later duplicates. Installed data actually
            // contains these (e.g. Reduce Signature), so rejecting the file is wrong.
            result.TryAdd(type, new(type, asset, Read("BuffToolTip"), Read("BuffAltToolTip"),
                Read("IsGoodBuff").Equals("TRUE", StringComparison.OrdinalIgnoreCase)));
        }
        return new(result.ToImmutable());
    }
}
