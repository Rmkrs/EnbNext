namespace EnbNext.Assets;

/// <summary>
/// Installed-retail faction display names from Data/client/ini/cfaction_t.ini.
/// Runtime faction keys remain case-sensitive; the INI reader's tolerant lookup
/// is not allowed to change the exact server-authored key join.
/// </summary>
public sealed class EnbFactionCatalog
{
    private readonly Dictionary<string, EnbFactionDefinition> _definitions;

    private EnbFactionCatalog(Dictionary<string, EnbFactionDefinition> definitions)
    {
        _definitions = definitions;
    }

    public IReadOnlyDictionary<string, EnbFactionDefinition> Definitions => _definitions;

    public static EnbFactionCatalog Load(string path)
    {
        EnbRetailIniDocument document = EnbRetailIniDocument.Load(path);
        var definitions = new Dictionary<string, EnbFactionDefinition>(StringComparer.Ordinal);
        foreach ((string key, IReadOnlyDictionary<string, string> values) in document.EnumerateSections())
        {
            string name=values.GetValueOrDefault("Name",key);
            string description=values.GetValueOrDefault("Description",key);
            definitions[key] = new EnbFactionDefinition(key, name, description);
        }
        return new EnbFactionCatalog(definitions);
    }

    public bool TryResolve(string key, out EnbFactionDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _definitions.TryGetValue(key, out definition!);
    }
}

public sealed record EnbFactionDefinition(string Key, string Name, string? Description);
