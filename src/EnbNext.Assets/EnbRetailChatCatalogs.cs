namespace EnbNext.Assets;

/// <summary>Current client.exe 005A5DA0/005A4B50/005A5080 named screen messages.</summary>
public sealed class EnbRetailEphemeralCatalog
{
    public sealed record Variant(string Style, int CumulativeChance);
    public sealed record Entry(string Text, IReadOnlyList<Variant> Variants, int TotalChance)
    {
        // The native random range includes TotalChance; that endpoint normally
        // selects nothing. Do not redistribute it among the authored styles.
        public string? SelectStyle(int draw) => Variants.FirstOrDefault(v =>
            draw < v.CumulativeChance && v.Style.Length != 0)?.Style;
    }
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    public Entry? Find(string name) => _entries.GetValueOrDefault(name);

    public static EnbRetailEphemeralCatalog Parse(EnbRetailIniDocument document)
    {
        var catalog = new EnbRetailEphemeralCatalog();
        for (int i = 0; document.TryGetSection($"Ephemeral-{i:00}", out var section); i++)
        {
            var variants = new List<Variant>();
            int total = 0;
            for (int j = 0; j < 5 && section.TryGetValue($"FontType{j:00}", out var style); j++)
            {
                float chance = section.TryGetValue($"FontChance{j:00}", out var raw) && EnbRetailMapperNumber.TryParse(raw, out var value) ? value : 0;
                // 005A4EF4..005A4EFC: FILD total, FADD chance, __ftol.
                total = (int)(total + (double)chance);
                variants.Add(new(style, total));
            }
            catalog._entries[section.GetValueOrDefault("Type", " ")] =
                new(section.GetValueOrDefault("Text", " "), variants, total);
        }
        return catalog;
    }
}

/// <summary>0057C480 reads consecutive SLASH sections, in authored order.</summary>
public sealed record EnbRetailSlashCommand(string Command, string Description)
{
    public static IReadOnlyList<EnbRetailSlashCommand> Parse(EnbRetailIniDocument document)
    {
        var entries = new List<EnbRetailSlashCommand>();
        for (int i = 0; document.TryGetSection($"SLASH-{i:000}", out var section); i++)
            entries.Add(new(section.GetValueOrDefault("Command", "Undefined"),
                section.GetValueOrDefault("Desc", "No Description available")));
        return entries;
    }
}
