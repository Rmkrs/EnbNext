using System.Globalization;

namespace EnbNext.Assets;

public enum EnbEnvironmentMapAxis
{
    X = 0,
    Y = 1,
    Z = 2,
}

public sealed record EnbEnvironmentProceduralTexture(int Index, string BaseTexture);

public sealed record EnbEnvironmentReflectionDefinition(
    int BaseAssetId,
    string Texture,
    EnbEnvironmentMapAxis Axis,
    bool UsesDefaultTexture);

/// <summary>
/// Retail envmaps.ini data used by the environment/reflection manager. Values
/// remain installation inputs; only the current executable's lookup/default
/// semantics are represented here.
/// </summary>
public sealed class EnbEnvironmentMapCatalog
{
    private readonly List<EnbEnvironmentProceduralTexture> _proceduralTextures = [];
    private readonly Dictionary<int, IReadOnlyDictionary<string, string>> _backSections = [];
    private string? _defaultTexture;

    public IReadOnlyList<EnbEnvironmentProceduralTexture> ProceduralTextures => _proceduralTextures;
    public string? DefaultReflectionTexture => _defaultTexture;

    public static EnbEnvironmentMapCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(EnbRetailIniDocument.Load(path));
    }

    public static EnbEnvironmentMapCatalog ParseText(string text)
        => Parse(EnbRetailIniDocument.Parse(text));

    public static EnbEnvironmentMapCatalog Parse(EnbRetailIniDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var result = new EnbEnvironmentMapCatalog();

        // Current retail enumerates PROCTEX-%02d from zero and stops on the
        // first missing section. BaseTexture falls back to missing_texture.tga.
        for (int index = 0; ; index++)
        {
            string sectionName = $"PROCTEX-{index:00}";
            if (!document.TryGetSection(sectionName, out IReadOnlyDictionary<string, string> values))
                break;

            string baseTexture = ReadNonEmpty(values, "BaseTexture") ?? "missing_texture.tga";
            result._proceduralTextures.Add(new EnbEnvironmentProceduralTexture(index, baseTexture));
        }

        if (document.TryGetSection("BACK-DEFAULT", out IReadOnlyDictionary<string, string> defaultValues))
            result._defaultTexture = ReadNonEmpty(defaultValues, "Texture");

        foreach ((string sectionName, IReadOnlyDictionary<string, string> values) in document.EnumerateSections())
        {
            if (!EnbRetailIniDocument.TryParseSectionId(sectionName, "BACK-", out int id))
                continue;
            result._backSections[id] = values;
        }

        return result;
    }

    public bool TryResolveReflection(int baseAssetId, out EnbEnvironmentReflectionDefinition definition)
    {
        _backSections.TryGetValue(baseAssetId, out IReadOnlyDictionary<string, string>? values);
        string? selectedTexture = values is null ? null : ReadNonEmpty(values, "Texture");
        bool usesDefault = string.IsNullOrEmpty(selectedTexture);
        selectedTexture ??= _defaultTexture;

        if (string.IsNullOrWhiteSpace(selectedTexture))
        {
            definition = default!;
            return false;
        }

        // Current retail reads Axis from the requested BACK-%i section. X and
        // Y are explicit; absent/unknown values select Z.
        EnbEnvironmentMapAxis axis = EnbEnvironmentMapAxis.Z;
        if (values is not null && values.TryGetValue("Axis", out string? rawAxis) && rawAxis.Length != 0)
        {
            axis = rawAxis.Trim()[0] switch
            {
                'X' or 'x' => EnbEnvironmentMapAxis.X,
                'Y' or 'y' => EnbEnvironmentMapAxis.Y,
                _ => EnbEnvironmentMapAxis.Z,
            };
        }

        definition = new EnbEnvironmentReflectionDefinition(baseAssetId, selectedTexture, axis, usesDefault);
        return true;
    }

    private static string? ReadNonEmpty(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
}

public enum EnbDecalDefinitionKind
{
    NormalTexture,
    Swatch,
    SecretTexture,
}

public sealed record EnbDecalDefinition(
    int DecalId,
    EnbDecalDefinitionKind Kind,
    string Resource,
    bool TextureWrap,
    IReadOnlyDictionary<string, string> Properties)
{
    /// <summary>The native mesh prototype token, distinct from the load filename.
    /// Null on an authored empty swatch slot: 0075BE00 retains its default descriptor.</summary>
    public string? SwatchPrototypeName { get; init; }
}

/// <summary>
/// Retail decal_textures.ini, decal_swatches.ini and secret_decal_textures.ini
/// namespace. The signed decal-id mapping is the current client mapping:
/// normal [0..], swatches [-1..], secret [1000..].
/// </summary>
public sealed class EnbDecalDefinitionCatalog
{
    private readonly List<EnbDecalDefinition> _normal = [];
    private readonly List<EnbDecalDefinition> _swatches = [];
    private readonly List<EnbDecalDefinition> _secret = [];
    private readonly Dictionary<int, EnbDecalDefinition> _byId = [];

    public IReadOnlyList<EnbDecalDefinition> NormalTextures => _normal;
    public IReadOnlyList<EnbDecalDefinition> Swatches => _swatches;
    public IReadOnlyList<EnbDecalDefinition> SecretTextures => _secret;

    public static EnbDecalDefinitionCatalog Load(
        string normalTexturePath,
        string swatchPath,
        string secretTexturePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalTexturePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(swatchPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretTexturePath);

        return Parse(
            EnbRetailIniDocument.Load(normalTexturePath),
            EnbRetailIniDocument.Load(swatchPath),
            EnbRetailIniDocument.Load(secretTexturePath));
    }

    public static EnbDecalDefinitionCatalog ParseText(
        string normalTextures,
        string swatches,
        string secretTextures)
        => Parse(
            EnbRetailIniDocument.Parse(normalTextures),
            EnbRetailIniDocument.Parse(swatches),
            EnbRetailIniDocument.Parse(secretTextures));

    public static EnbDecalDefinitionCatalog Parse(
        EnbRetailIniDocument normalTextures,
        EnbRetailIniDocument swatches,
        EnbRetailIniDocument secretTextures)
    {
        ArgumentNullException.ThrowIfNull(normalTextures);
        ArgumentNullException.ThrowIfNull(swatches);
        ArgumentNullException.ThrowIfNull(secretTextures);

        var result = new EnbDecalDefinitionCatalog();
        LoadTextureFamily(normalTextures, result._normal, result._byId, secret: false);
        LoadSwatches(swatches, result._swatches, result._byId);
        LoadTextureFamily(secretTextures, result._secret, result._byId, secret: true);
        return result;
    }

    public bool TryResolve(int decalId, out EnbDecalDefinition definition)
        => _byId.TryGetValue(decalId, out definition!);

    private static void LoadTextureFamily(
        EnbRetailIniDocument document,
        List<EnbDecalDefinition> destination,
        Dictionary<int, EnbDecalDefinition> byId,
        bool secret)
    {
        for (int index = 0; ; index++)
        {
            string sectionName = $"Decal{index:0000}";
            if (!document.TryGetSection(sectionName, out IReadOnlyDictionary<string, string> values))
                break;

            string file = ReadNonEmpty(values, "File") ?? "default.tga";
            bool wrap = !values.TryGetValue("TextureWrap", out string? rawWrap) || ParseRetailBool(rawWrap, defaultValue: true);
            int decalId = secret ? checked(1000 + index) : index;
            var definition = new EnbDecalDefinition(
                decalId,
                secret ? EnbDecalDefinitionKind.SecretTexture : EnbDecalDefinitionKind.NormalTexture,
                file,
                wrap,
                Copy(values));
            destination.Add(definition);
            byId[decalId] = definition;
        }
    }

    private static void LoadSwatches(
        EnbRetailIniDocument document,
        List<EnbDecalDefinition> destination,
        Dictionary<int, EnbDecalDefinition> byId)
    {
        for (int index = 0; ; index++)
        {
            string sectionName = $"Decal{index:00}";
            if (!document.TryGetSection(sectionName, out IReadOnlyDictionary<string, string> values))
                break;

            // 0075BE00 appends a descriptor for every sequential section,
            // including an empty Swatch value. Skipping it loses a valid slot.
            string? swatch = ReadNonEmpty(values, "Swatch");

            int decalId = checked(-index - 1);
            var definition = new EnbDecalDefinition(
                decalId,
                EnbDecalDefinitionKind.Swatch,
                swatch is null ? string.Empty : swatch + ".w3d",
                TextureWrap: true,
                Copy(values)) { SwatchPrototypeName = swatch };
            destination.Add(definition);
            byId[decalId] = definition;
        }
    }

    private static bool ParseRetailBool(string? value, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        return value.Trim() switch
        {
            "1" => true,
            "0" => false,
            var v when v.Equals("TRUE", StringComparison.OrdinalIgnoreCase) => true,
            var v when v.Equals("YES", StringComparison.OrdinalIgnoreCase) => true,
            var v when v.Equals("ON", StringComparison.OrdinalIgnoreCase) => true,
            var v when v.Equals("FALSE", StringComparison.OrdinalIgnoreCase) => false,
            var v when v.Equals("NO", StringComparison.OrdinalIgnoreCase) => false,
            var v when v.Equals("OFF", StringComparison.OrdinalIgnoreCase) => false,
            _ => defaultValue,
        };
    }

    private static string? ReadNonEmpty(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string> values)
        => new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
}

public readonly record struct EnbRgb(float R, float G, float B)
{
    public bool IsFinite => float.IsFinite(R) && float.IsFinite(G) && float.IsFinite(B);

    internal static bool TryParse(string? raw, out EnbRgb color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string value = raw;
        int semicolon = value.IndexOf(';');
        if (semicolon >= 0)
            value = value[..semicolon];

        string[] pieces = value.Split(',', StringSplitOptions.TrimEntries);
        if (pieces.Length < 3 ||
            !float.TryParse(pieces[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) ||
            !float.TryParse(pieces[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g) ||
            !float.TryParse(pieces[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b) ||
            !float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b))
        {
            return false;
        }

        color = new EnbRgb(r, g, b);
        return true;
    }
}

public sealed class EnbMetalEquivalentRgbCatalog
{
    private readonly Dictionary<int, EnbRgb> _colors = [];

    public int Count => _colors.Count;

    public static EnbMetalEquivalentRgbCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(EnbRetailIniDocument.Load(path));
    }

    public static EnbMetalEquivalentRgbCatalog ParseText(string text)
        => Parse(EnbRetailIniDocument.Parse(text));

    public static EnbMetalEquivalentRgbCatalog Parse(EnbRetailIniDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new EnbMetalEquivalentRgbCatalog();

        foreach ((string name, IReadOnlyDictionary<string, string> values) in document.EnumerateSections())
        {
            if (!EnbRetailIniDocument.TryParseSectionId(name, "Metal", out int id))
            {
                continue;
            }

            // 0080F52E/542/55F pass 1.0 independently to each INI lookup.
            // Keep reading the authored Metal00 names: retail's Metal-%02d
            // lookup disagrees with its shipped metal_equivalent_rgbs.ini.
            result._colors[id] = new EnbRgb(Channel("R"), Channel("G"), Channel("B"));
            float Channel(string key)
            {
                if (!values.TryGetValue(key, out var raw)) return 1f;
                float value = EnbRetailMapperNumber.TryParseOperand(raw, out var parsed, out bool percentage, requireFinite: false)
                    ? parsed : 1f;
                return percentage ? value * .01f : value;
            }
        }

        return result;
    }

    public bool TryResolve(int metalId, out EnbRgb color)
        => _colors.TryGetValue(metalId, out color);

    public EnbRgb ResolveOrDefault(int metalId)
        => _colors.GetValueOrDefault(metalId, new EnbRgb(1, 1, 1));
}

public sealed record EnbGutterColorDefinition(
    string Category,
    IReadOnlyDictionary<string, EnbRgb> Colors);

public sealed class EnbGutterColorCatalog
{
    private readonly Dictionary<string, EnbGutterColorDefinition> _categories = new(StringComparer.OrdinalIgnoreCase);

    public int CategoryCount => _categories.Count;

    public static EnbGutterColorCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(EnbRetailIniDocument.Load(path));
    }

    public static EnbGutterColorCatalog ParseText(string text)
        => Parse(EnbRetailIniDocument.Parse(text));

    public static EnbGutterColorCatalog Parse(EnbRetailIniDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new EnbGutterColorCatalog();

        foreach ((string category, IReadOnlyDictionary<string, string> values) in document.EnumerateSections())
        {
            var colors = new Dictionary<string, EnbRgb>(StringComparer.OrdinalIgnoreCase);
            foreach ((string key, string raw) in values)
            {
                if (EnbRgb.TryParse(raw, out EnbRgb color))
                    colors[key] = color;
            }

            result._categories[category] = new EnbGutterColorDefinition(category, colors);
        }

        return result;
    }

    public bool TryResolve(string category, string key, out EnbRgb color)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        color = default;
        return _categories.TryGetValue(category, out EnbGutterColorDefinition? definition)
            && definition.Colors.TryGetValue(key, out color);
    }
}

public sealed record EnbFontDefinition(
    int Slot,
    string? FontId,
    string? Resource,
    float Scale,
    int Spacing)
{
    public int LineSpacing { get; init; }
}

public sealed record EnbFontProfile(
    int Index,
    int ResWidth,
    int ResHeight,
    IReadOnlyList<EnbFontDefinition> Fonts,
    IReadOnlyDictionary<string, string> ResolvedProperties);

public sealed class EnbFontSystemCatalog
{
    private readonly List<EnbFontProfile> _profiles = [];

    public IReadOnlyList<EnbFontProfile> Profiles => _profiles;
    public IReadOnlyList<string> AvailableFonts { get; private set; } = [];

    public static EnbFontSystemCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(EnbRetailIniDocument.Load(path));
    }

    public static EnbFontSystemCatalog ParseText(string text)
        => Parse(EnbRetailIniDocument.Parse(text));

    public static EnbFontSystemCatalog Parse(EnbRetailIniDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new EnbFontSystemCatalog();
        var resolver = new EnbRetailTemplateResolver(document);

        if (document.TryGetSection("ALL_FONTS", out var allFonts))
            result.AvailableFonts = Enumerable.Range(0, allFonts.Count).Select(i => allFonts.GetValueOrDefault(i.ToString(CultureInfo.InvariantCulture))).TakeWhile(v => v is not null).Cast<string>().ToArray();
        var profiles = new List<(int Index, string Section)>();
        foreach (string section in document.SectionNames)
        {
            if (EnbRetailIniDocument.TryParseSectionId(section, "INIT-", out int index) && index >= 0)
                profiles.Add((index, section));
        }
        profiles.Sort(static (a, b) => a.Index.CompareTo(b.Index));

        foreach ((int index, string section) in profiles)
        {
            IReadOnlyDictionary<string, string> values = resolver.Resolve(section);
            int width = ReadInt(values, "ResWidth", 0);
            int height = ReadInt(values, "ResHeight", 0);
            var fonts = new List<EnbFontDefinition>(8);

            for (int slot = 0; slot < 8; slot++)
            {
                values.TryGetValue($"FontID{slot}", out string? fontId);
                values.TryGetValue($"Font{slot}", out string? resource);
                float scale = ReadFloat(values, $"Scale{slot}", 0f);
                int spacing = ReadInt(values, $"Spacing{slot}", 0);
                if (!string.IsNullOrWhiteSpace(fontId) || !string.IsNullOrWhiteSpace(resource))
                {
                    fonts.Add(new EnbFontDefinition(slot, EmptyToNull(fontId), EmptyToNull(resource), scale, spacing));
                }
            }

            result._profiles.Add(new EnbFontProfile(index, width, height, fonts, values));
        }

        return result;
    }

    /// <summary>Retail keeps the last loaded profile that fits both display dimensions.</summary>
    public bool TrySelectProfile(int displayWidth, int displayHeight, out EnbFontProfile profile)
    {
        profile = default!;
        bool found = false;
        foreach (EnbFontProfile candidate in _profiles)
        {
            if (candidate.ResWidth <= displayWidth && candidate.ResHeight <= displayHeight)
            {
                profile = candidate;
                found = true;
            }
        }
        return found;
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
        => EnbRetailIniDocument.TryReadInt(values, key, out int value) ? value : fallback;

    private static float ReadFloat(IReadOnlyDictionary<string, string> values, string key, float fallback)
        => EnbRetailIniDocument.TryReadFloat(values, key, out float value) ? value : fallback;

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public enum EnbInterfaceDefinitionKind
{
    String,
    Wrap,
}

public sealed record EnbInterfaceTextDefinition(
    string StringId,
    EnbInterfaceDefinitionKind Kind,
    string SourceSection,
    IReadOnlyDictionary<string, string> Properties);

/// <summary>
/// Asset-native interface StringID/template catalog used by files such as
/// fc_radar.ini and fc_cpit.ini. It preserves resolved authored properties;
/// The runtime UI owns the eventual interpretation.
/// </summary>
public sealed class EnbInterfaceLayoutCatalog
{
    private readonly Dictionary<string, EnbInterfaceTextDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _definitions.Count;
    public IEnumerable<EnbInterfaceTextDefinition> Definitions => _definitions.Values;

    public static EnbInterfaceLayoutCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(EnbRetailIniDocument.Load(path));
    }

    public static EnbInterfaceLayoutCatalog ParseText(string text)
        => Parse(EnbRetailIniDocument.Parse(text));

    public static EnbInterfaceLayoutCatalog Parse(EnbRetailIniDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new EnbInterfaceLayoutCatalog();
        var resolver = new EnbRetailTemplateResolver(document);

        var sections = new List<(int Order, EnbInterfaceDefinitionKind Kind, string Section)>();
        foreach (string section in document.SectionNames)
        {
            if (EnbRetailIniDocument.TryParseSectionId(section, "STR-", out int strIndex) && strIndex >= 0)
                sections.Add((strIndex * 2, EnbInterfaceDefinitionKind.String, section));
            else if (EnbRetailIniDocument.TryParseSectionId(section, "WRAP-", out int wrapIndex) && wrapIndex >= 0)
                sections.Add((wrapIndex * 2 + 1, EnbInterfaceDefinitionKind.Wrap, section));
        }
        sections.Sort(static (a, b) => a.Order.CompareTo(b.Order));

        foreach ((_, EnbInterfaceDefinitionKind kind, string section) in sections)
        {
            IReadOnlyDictionary<string, string> values = resolver.Resolve(section);
            if (!values.TryGetValue("StringID", out string? stringId) || string.IsNullOrWhiteSpace(stringId))
                continue;

            string normalizedId = stringId.Trim();
            result._definitions[normalizedId] = new EnbInterfaceTextDefinition(normalizedId, kind, section, values);
        }

        return result;
    }

    public bool TryResolve(string stringId, out EnbInterfaceTextDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stringId);
        return _definitions.TryGetValue(stringId, out definition!);
    }
}

/// <summary>
/// Resolves the retail Template%d inheritance convention used by interface and
/// font records. Templates are applied in numeric order before local values.
/// Cycles are cut deterministically rather than recursing indefinitely.
/// </summary>
internal sealed class EnbRetailTemplateResolver
{
    private readonly EnbRetailIniDocument _document;
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public EnbRetailTemplateResolver(EnbRetailIniDocument document)
    {
        _document = document;
    }

    public IReadOnlyDictionary<string, string> Resolve(string sectionName)
        => Resolve(sectionName, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private IReadOnlyDictionary<string, string> Resolve(string sectionName, HashSet<string> active)
    {
        if (_cache.TryGetValue(sectionName, out IReadOnlyDictionary<string, string>? cached))
            return cached;

        if (!_document.TryGetSection(sectionName, out IReadOnlyDictionary<string, string> local))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!active.Add(sectionName))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int missesRemaining = 10;
        for (int index = 0; missesRemaining > 0; index++)
        {
            string templateKey = $"Template{index}";
            if (!local.TryGetValue(templateKey, out string? templateName) || string.IsNullOrWhiteSpace(templateName))
            {
                missesRemaining--;
                continue;
            }

            missesRemaining = 10;
            foreach ((string key, string value) in Resolve(templateName.Trim(), active))
                merged[key] = value;
        }

        foreach ((string key, string value) in local)
        {
            if (!IsTemplateKey(key))
                merged[key] = value;
        }

        active.Remove(sectionName);
        var frozen = new Dictionary<string, string>(merged, StringComparer.OrdinalIgnoreCase);
        _cache[sectionName] = frozen;
        return frozen;
    }

    private static bool IsTemplateKey(string key)
    {
        if (!key.StartsWith("Template", StringComparison.OrdinalIgnoreCase))
            return false;
        return int.TryParse(key.AsSpan("Template".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }
}
