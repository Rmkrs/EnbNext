namespace EnbNext.Assets;

public sealed record EnbProjectedDecalDefinition(float BaseSize, string Texture, bool Lighting);

/// <summary>00763050: sequential Decal%03d entries in installed proj_dec.ini.</summary>
public sealed class EnbProjectedDecalCatalog
{
    public IReadOnlyList<EnbProjectedDecalDefinition> Entries { get; }

    public EnbProjectedDecalCatalog(EnbRetailIniDocument document)
    {
        var entries = new List<EnbProjectedDecalDefinition>();
        for (int i = 0; document.TryGetSection($"Decal{i:000}", out var section); i++)
            entries.Add(new(
                EnbRetailIniDocument.TryReadFloat(section, "BaseSize", out var size) ? size : 0,
                section.GetValueOrDefault("Texture", ""),
                EnbRetailIniDocument.ReadBool(section, "Lighting")));
        Entries = entries;
    }
}
