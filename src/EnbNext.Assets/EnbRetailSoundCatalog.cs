using System.Globalization;

namespace EnbNext.Assets;

/// <summary>
/// One weighted authored entry from a retail sounds.ini N\Files subsection.
/// A NULL key is preserved as an explicit silent choice because current retail
/// treats it as a valid weighted entry rather than inventing a filename.
/// </summary>
public sealed record EnbRetailSoundFileChoice(
    string? LogicalResourceName,
    int? Weight,
    bool IsNullChoice);

/// <summary>
/// Current-retail authored sound definition. Values remain nullable when neither
/// the sound section nor SOUND_DEFAULTS authors them; this layer does not invent
/// presentation values merely because the retail binary has emergency defaults.
/// </summary>
public sealed record EnbRetailSoundDefinition(
    string Name,
    IReadOnlyList<EnbRetailSoundFileChoice> Files,
    int? LoopCount,
    float? DropOffRadius,
    float? MaxVolumeRadius,
    float? Priority,
    float? Volume,
    float? MinVolumeShift,
    float? MaxVolumeShift,
    float? MinPitchShift,
    float? MaxPitchShift,
    float? MinDopplerShift,
    float? MaxDopplerShift,
    float? DopplerTweak,
    int? SoundType,
    int? MaxInstances,
    float? Pitch,
    float? SecondsBetweenCalls,
    IReadOnlyDictionary<string, string> Properties)
{
    // These are current client.exe constructor fallbacks, not authored game data.
    // sounds.ini / SOUND_DEFAULTS always win when they author the corresponding
    // value. Keeping the distinction explicit prevents an installation-specific
    // value from turning into a compiled EnbNext constant while still reproducing
    // retail behavior for omitted keys.
    public int EffectiveLoopCount => LoopCount ?? 1;
    public float EffectiveDropOffRadius => DropOffRadius ?? 1200f;
    public float EffectiveMaxVolumeRadius => MaxVolumeRadius ?? 400f;
    public float EffectivePriority => Priority ?? 0.9f;
    public float EffectiveVolume => Volume ?? 0.8f;
    public float EffectiveMinVolumeShift => MinVolumeShift ?? 0f;
    public float EffectiveMaxVolumeShift => MaxVolumeShift ?? 0f;
    public float EffectiveMinPitchShift => MinPitchShift ?? 0f;
    public float EffectiveMaxPitchShift => MaxPitchShift ?? 0f;
    public float EffectiveMinDopplerShift => MinDopplerShift ?? 1f;
    public float EffectiveMaxDopplerShift => MaxDopplerShift ?? 1f;
    public float EffectiveDopplerTweak => DopplerTweak ?? 0.1f;
    public int EffectiveSoundType => SoundType ?? 1;
    public int EffectiveMaxInstances => MaxInstances ?? 4;
    public float EffectivePitch => Pitch ?? 1f;
    public float EffectiveSecondsBetweenCalls => SecondsBetweenCalls ?? 0f;

    public bool UsesRetailFallbackForSoundType => !SoundType.HasValue;
}

public enum EnbRetailSoundResourceStatus
{
    NullChoice,
    LooseFile,
    MixResource,
    Missing,
}

public sealed record EnbRetailSoundResolvedChoice(
    EnbRetailSoundFileChoice Choice,
    EnbRetailSoundResourceStatus Status,
    string? LoosePath,
    EnbMixResourceLocation? MixLocation,
    IReadOnlyList<EnbMixResourceLocation> MixCandidates);

public sealed record EnbRetailSoundResolution(
    EnbRetailSoundDefinition Definition,
    IReadOnlyList<EnbRetailSoundResolvedChoice> Choices)
{
    public bool IsComplete => Choices.Count != 0 && Choices.All(static choice =>
        choice.Status is EnbRetailSoundResourceStatus.NullChoice or
            EnbRetailSoundResourceStatus.LooseFile or
            EnbRetailSoundResourceStatus.MixResource);
}

/// <summary>
/// Read-only current retail sounds.ini catalog. Current client initialization
/// resolves sounds.ini through its ordinary file manager, parses SOUND_DEFAULTS,
/// then constructs one definition for each non-"\\Files" section. A definition
/// uses SoundFilename when present; otherwise it consumes weighted entries from
/// the sibling "section\\Files" subsection.
/// </summary>
public sealed class EnbRetailSoundCatalog
{
    private const string DefaultsSection = "SOUND_DEFAULTS";
    private readonly Dictionary<string, EnbRetailSoundDefinition> _definitions;
    private IReadOnlyDictionary<string,string> _defaults = EmptyProperties;

    private EnbRetailSoundCatalog(Dictionary<string, EnbRetailSoundDefinition> definitions)
    {
        _definitions = definitions;
        Definitions = definitions.Values
            .OrderBy(static definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<EnbRetailSoundDefinition> Definitions { get; }

    public int Count => _definitions.Count;

    public static EnbRetailSoundCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(EnbRetailIniDocument.Load(path));
    }

    public static EnbRetailSoundCatalog Parse(EnbRetailIniDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        IReadOnlyDictionary<string, string> defaults =
            document.TryGetSection(DefaultsSection, out IReadOnlyDictionary<string, string> authoredDefaults)
                ? authoredDefaults
                : EmptyProperties;

        var definitions = new Dictionary<string, EnbRetailSoundDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach ((string sectionName, IReadOnlyDictionary<string, string> properties) in document.EnumerateSections())
        {
            if (sectionName.Equals(DefaultsSection, StringComparison.OrdinalIgnoreCase) ||
                sectionName.Contains("\\Files", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            IReadOnlyList<EnbRetailSoundFileChoice> files = ParseFiles(document, sectionName, properties);
            definitions[sectionName] = new EnbRetailSoundDefinition(
                sectionName,
                files,
                ReadInt(properties, "SoundLoopCount"),
                ReadFloat(properties, defaults, "SoundDropOffRadius", "Default_DropOffRadius"),
                ReadFloat(properties, defaults, "SoundMaxVolRadius", "Default_MaxVolRadius"),
                ReadFloat(properties, defaults, "SoundPriority", "Default_Priority"),
                ReadFloat(properties, defaults, "SoundVolume", "Default_Volume"),
                ReadFloat(properties, "SoundMinVolumeShift"),
                ReadFloat(properties, "SoundMaxVolumeShift"),
                ReadFloat(properties, "SoundMinPitchShift"),
                ReadFloat(properties, "SoundMaxPitchShift"),
                ReadFloat(properties, "SoundMinDopplerShift"),
                ReadFloat(properties, "SoundMaxDopplerShift"),
                ReadFloat(properties, "SoundDopplerTweak"),
                ReadInt(properties, "SoundType"),
                ReadInt(properties, defaults, "SoundMaxInstances", "Default_MaxInstances"),
                ReadFloat(properties, "SoundPitch"),
                ReadFloat(properties, "SoundSecsBetweenCalls"),
                properties);
        }

        return new EnbRetailSoundCatalog(definitions) { _defaults = defaults };
    }

    // Current 0043EFE0/0043ED30 first look up sounds.ini, then construct a
    // direct-file definition using the initialized SOUND_DEFAULTS (0043DF90).
    public EnbRetailSoundDefinition GetDefinitionOrFile(string name)
    {
        if (TryGetDefinition(name,out var definition)) return definition;
        return GetFileDefinition(name);
    }

    // 0043F2A0 -> 0043F2D0 constructs a filename definition directly; it
    // does not resolve a same-named sounds.ini preset. 0043FE30 loads the
    // separate Default_Dialog_Volume used by that construction and queue drain.
    public EnbRetailSoundDefinition GetSpeechFileDefinition(string name)
        => GetFileDefinition(name) with
        {
            SoundType = 2,
            Volume = ReadFloat(_defaults, "Default_Dialog_Volume") ?? 0.8f
        };

    private EnbRetailSoundDefinition GetFileDefinition(string name)
    {
        return new(name,[new(name,null,false)],1,
            ReadFloat(_defaults,"Default_DropOffRadius"), ReadFloat(_defaults,"Default_MaxVolRadius"),
            ReadFloat(_defaults,"Default_Priority"), ReadFloat(_defaults,"Default_Volume"),
            null,null,null,null,null,null,null,1,ReadInt(_defaults,"Default_MaxInstances"),1,0,EmptyProperties);
    }

    public bool TryGetDefinition(string logicalSoundName, out EnbRetailSoundDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalSoundName);
        return _definitions.TryGetValue(logicalSoundName.Trim(), out definition!);
    }

    public EnbRetailSoundResolution Resolve(
        EnbRetailSoundDefinition definition,
        EnbInstalledAssetPaths paths,
        EnbMixResourceCatalog? retailMixResources)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(paths);

        var choices = new List<EnbRetailSoundResolvedChoice>(definition.Files.Count);
        foreach (EnbRetailSoundFileChoice choice in definition.Files)
        {
            if (choice.IsNullChoice || string.IsNullOrWhiteSpace(choice.LogicalResourceName))
            {
                choices.Add(new EnbRetailSoundResolvedChoice(
                    choice,
                    EnbRetailSoundResourceStatus.NullChoice,
                    LoosePath: null,
                    MixLocation: null,
                    MixCandidates: Array.Empty<EnbMixResourceLocation>()));
                continue;
            }

            string logicalName = choice.LogicalResourceName!;
            if (paths.TryResolveRetailLooseResource(logicalName, out string loosePath))
            {
                choices.Add(new EnbRetailSoundResolvedChoice(
                    choice,
                    EnbRetailSoundResourceStatus.LooseFile,
                    loosePath,
                    MixLocation: null,
                    MixCandidates: Array.Empty<EnbMixResourceLocation>()));
                continue;
            }

            if (retailMixResources is null ||
                !retailMixResources.TryGetResourceLocations(logicalName, out IReadOnlyList<EnbMixResourceLocation> locations))
            {
                choices.Add(new EnbRetailSoundResolvedChoice(
                    choice,
                    EnbRetailSoundResourceStatus.Missing,
                    LoosePath: null,
                    MixLocation: null,
                    MixCandidates: Array.Empty<EnbMixResourceLocation>()));
                continue;
            }

            // Retail 004E5440 keeps the first enumerated package for a duplicate name.
            choices.Add(new EnbRetailSoundResolvedChoice(
                choice,
                EnbRetailSoundResourceStatus.MixResource,
                LoosePath: null,
                locations[0],
                locations));
        }

        return new EnbRetailSoundResolution(definition, choices);
    }

    private static IReadOnlyList<EnbRetailSoundFileChoice> ParseFiles(
        EnbRetailIniDocument document,
        string sectionName,
        IReadOnlyDictionary<string, string> properties)
    {
        if (properties.TryGetValue("SoundFilename", out string? fileName) &&
            !string.IsNullOrWhiteSpace(fileName))
        {
            return [new EnbRetailSoundFileChoice(fileName.Trim(), Weight: null, IsNullChoice: false)];
        }

        if (!document.TryGetSection($"{sectionName}\\Files", out IReadOnlyDictionary<string, string> fileProperties))
            return Array.Empty<EnbRetailSoundFileChoice>();

        var files = new List<EnbRetailSoundFileChoice>();
        foreach ((string name, string rawWeight) in fileProperties)
        {
            if (!int.TryParse(rawWeight, NumberStyles.Integer, CultureInfo.InvariantCulture, out int weight) ||
                weight <= 0)
            {
                continue;
            }

            bool isNull = name.Equals("NULL", StringComparison.OrdinalIgnoreCase);
            files.Add(new EnbRetailSoundFileChoice(isNull ? null : name, weight, isNull));
        }
        return files;
    }

    private static int? ReadInt(
        IReadOnlyDictionary<string, string> properties,
        string key)
        => EnbRetailIniDocument.TryReadInt(properties, key, out int value) ? value : null;

    private static float? ReadFloat(
        IReadOnlyDictionary<string, string> properties,
        string key)
        => EnbRetailIniDocument.TryReadFloat(properties, key, out float value) ? value : null;

    private static int? ReadInt(
        IReadOnlyDictionary<string, string> properties,
        IReadOnlyDictionary<string, string> defaults,
        string key,
        string? defaultKey = null)
    {
        if (EnbRetailIniDocument.TryReadInt(properties, key, out int value))
            return value;
        if (EnbRetailIniDocument.TryReadInt(defaults, defaultKey ?? key, out value))
            return value;
        return null;
    }

    private static float? ReadFloat(
        IReadOnlyDictionary<string, string> properties,
        IReadOnlyDictionary<string, string> defaults,
        string key,
        string? defaultKey = null)
    {
        if (EnbRetailIniDocument.TryReadFloat(properties, key, out float value))
            return value;
        if (EnbRetailIniDocument.TryReadFloat(defaults, defaultKey ?? key, out value))
            return value;
        return null;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyProperties =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
