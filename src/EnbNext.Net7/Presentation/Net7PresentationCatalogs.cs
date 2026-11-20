using System.Globalization;

namespace EnbNext.Net7.Presentation;

/// <summary>
/// Current-client BaseAsset resolver branches. The selector remains
/// distinct from GameID and from any renderer/resource handle produced later.
/// </summary>
public enum Net7BaseAssetDefinitionKind
{
    Missing,
    NullObject,
    Rsl,
    FileName,
    ProceduralWidth,
    Unsupported,
}

public sealed record Net7BaseAssetDefinition(
    int Selector,
    Net7BaseAssetDefinitionKind Kind,
    bool IsCatalogEntry,
    int? RslId = null,
    int? LinkId = null,
    int? RsId = null,
    string? File = null,
    string? Name = null,
    float? Width = null,
    bool OverbrightOff = false,
    IReadOnlyDictionary<string, string>? Properties = null)
{
    public bool IsMissing => Kind == Net7BaseAssetDefinitionKind.Missing;
}

/// <summary>The thirteen exact EffectClass strings.</summary>
public enum Net7EffectClassKind
{
    Unknown,
    RObjLookAtEffect,
    RObjEffect,
    WarpEffect,
    LineEffect,
    StraightSeekEffect,
    SpiralSeekEffect,
    SplineRandomSeekEffect,
    LinearRandomSeekEffect,
    SoundEffect,
    PassEffect,
    AttachEffect,
    RenderStateEffect,
    ParticleJetEffect,
}

public sealed record Net7EffectDefinition(
    int DefinitionId,
    string EffectClass,
    Net7EffectClassKind EffectClassKind,
    int? BaseAssetId,
    int? StartLinkId,
    int? NextLinkId,
    string? DurationRaw,
    bool InfiniteDuration,
    float? DurationValue,
    float? StartDelay,
    float? StartLinkDelay,
    float? CycleTime,
    bool UnaryStartLinkToTarget,
    bool UnaryNextLinkToTarget,
    bool TrackTarget,
    bool TrackSource,
    bool GoAwayWhenTargetDoes,
    bool GoAwayWhenSourceDoes,
    bool OffsetDurationPassedToStartLink,
    bool PassDurationToStartLink,
    bool DurationLinkedEffectUseNextLink,
    bool DurationLinkedEffectUseStartLink,
    string? Sfx,
    IReadOnlyDictionary<string, string> Properties)
{
    /// <summary>
    /// Current constructors directly route BaseAssetID for ten classes.
    /// SoundEffect, PassEffect and RenderStateEffect do not take that direct route.
    /// </summary>
    public bool UsesBaseAssetDirectly => EffectClassKind is
        Net7EffectClassKind.RObjLookAtEffect or
        Net7EffectClassKind.RObjEffect or
        Net7EffectClassKind.WarpEffect or
        Net7EffectClassKind.LineEffect or
        Net7EffectClassKind.StraightSeekEffect or
        Net7EffectClassKind.SpiralSeekEffect or
        Net7EffectClassKind.SplineRandomSeekEffect or
        Net7EffectClassKind.LinearRandomSeekEffect or
        Net7EffectClassKind.AttachEffect or
        Net7EffectClassKind.ParticleJetEffect;
}

public sealed record Net7EffectPresentationResolution(
    int DefinitionId,
    Net7EffectDefinition? Definition,
    Net7BaseAssetDefinition? BaseAsset,
    bool MissingDefinition,
    bool InvalidDefinition,
    bool MissingBaseAsset);

public sealed record Net7EffectGraphResolution(
    int RootDefinitionId,
    IReadOnlyList<Net7EffectDefinition> Definitions,
    IReadOnlyList<int> MissingDefinitionIds,
    bool CycleDetected,
    bool TraversalLimitReached);

/// <summary>
/// Local presentation catalogs required. Loading or
/// resolving these definitions never emits a server packet. A missing local
/// definition is therefore a presentation result, not a protocol retry trigger.
/// </summary>
public sealed class Net7PresentationCatalogs
{
    private readonly Dictionary<int, IReadOnlyDictionary<string, string>> _baseSections = [];
    private readonly Dictionary<int, Net7EffectDefinition> _effects = [];

    public int BaseAssetCount => _baseSections.Count;
    public int EffectDefinitionCount => _effects.Count;

    public void Clear()
    {
        _baseSections.Clear();
        _effects.Clear();
    }

    public void LoadBaseAssets(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        LoadBaseAssetsText(File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(path)));
    }

    public void LoadEffects(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        LoadEffectsText(File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(path)));
    }

    public void LoadBaseAssetsText(string text)
    {
        Dictionary<string, Dictionary<string, string>> sections = ParseIni(text);
        _baseSections.Clear();
        foreach ((string name, Dictionary<string, string> values) in sections)
        {
            if (TryParseSectionId(name, "BASE-", out int id))
                _baseSections[id] = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void LoadEffectsText(string text)
    {
        Dictionary<string, Dictionary<string, string>> sections = ParseIni(text);
        _effects.Clear();
        foreach ((string name, Dictionary<string, string> values) in sections)
        {
            if (!TryParseSectionId(name, "EFFECT-", out int id))
                continue;
            _effects[id] = BuildEffectDefinition(id, values);
        }
    }

    public Net7BaseAssetDefinition ResolveBaseAsset(int selector)
    {
        // This explicitly closes -1 as the no-local-presentation selector.
        if (selector == -1)
            return new Net7BaseAssetDefinition(selector, Net7BaseAssetDefinitionKind.NullObject, IsCatalogEntry: false);

        if (!_baseSections.TryGetValue(selector, out IReadOnlyDictionary<string, string>? values))
            return new Net7BaseAssetDefinition(selector, Net7BaseAssetDefinitionKind.Missing, IsCatalogEntry: false);

        bool overbrightOff = ReadBool(values, "OverbrightOff");
        if (values.ContainsKey("NULLOBJECT"))
        {
            return new Net7BaseAssetDefinition(
                selector, Net7BaseAssetDefinitionKind.NullObject, true,
                OverbrightOff: overbrightOff, Properties: values);
        }

        if (values.ContainsKey("RSLID"))
        {
            // Current retail commits to the RSLID branch based on property
            // presence. ConfigGetInteger then uses -1 as the invalid/missing
            // sentinel; it does not fall through to FILE/NAME or Width.
            if (!TryReadInt(values, "RSLID", out int rslId) || rslId == -1)
            {
                return new Net7BaseAssetDefinition(
                    selector, Net7BaseAssetDefinitionKind.Missing, true,
                    OverbrightOff: overbrightOff, Properties: values);
            }

            int? linkId = TryReadInt(values, "LINKID", out int parsedLinkId) && parsedLinkId != -1
                ? parsedLinkId
                : null;
            int? rsId = TryReadInt(values, "RSID", out int parsedRsId) && parsedRsId != -1
                ? parsedRsId
                : null;
            return new Net7BaseAssetDefinition(
                selector, Net7BaseAssetDefinitionKind.Rsl, true,
                RslId: rslId,
                LinkId: linkId,
                RsId: rsId,
                OverbrightOff: overbrightOff,
                Properties: values);
        }

        if (values.TryGetValue("FILE", out string? file) && values.TryGetValue("NAME", out string? objectName))
        {
            return new Net7BaseAssetDefinition(
                selector, Net7BaseAssetDefinitionKind.FileName, true,
                File: file, Name: objectName, OverbrightOff: overbrightOff, Properties: values);
        }

        if (TryReadFloat(values, "Width", out float width))
        {
            return new Net7BaseAssetDefinition(
                selector, Net7BaseAssetDefinitionKind.ProceduralWidth, true,
                Width: width, OverbrightOff: overbrightOff, Properties: values);
        }

        return new Net7BaseAssetDefinition(
            selector, Net7BaseAssetDefinitionKind.Unsupported, true,
            OverbrightOff: overbrightOff, Properties: values);
    }

    public Net7EffectDefinition? FindEffect(int definitionId)
        => _effects.TryGetValue(definitionId, out Net7EffectDefinition? definition) ? definition : null;

    public Net7EffectPresentationResolution ResolveEffect(int definitionId)
    {
        if (!_effects.TryGetValue(definitionId, out Net7EffectDefinition? definition))
            return new Net7EffectPresentationResolution(
                definitionId, null, null, MissingDefinition: true, InvalidDefinition: false, MissingBaseAsset: false);

        if (definition.EffectClassKind == Net7EffectClassKind.Unknown)
        {
            return new Net7EffectPresentationResolution(
                definitionId, definition, null, MissingDefinition: false, InvalidDefinition: true, MissingBaseAsset: false);
        }

        Net7BaseAssetDefinition? baseAsset = null;
        bool missingBaseAsset = false;
        if (definition.UsesBaseAssetDirectly && definition.BaseAssetId is int baseAssetId)
        {
            baseAsset = ResolveBaseAsset(baseAssetId);
            missingBaseAsset = baseAsset.IsMissing;
        }

        return new Net7EffectPresentationResolution(
            definitionId, definition, baseAsset, MissingDefinition: false, InvalidDefinition: false, MissingBaseAsset: missingBaseAsset);
    }

    /// <summary>
    /// Resolve StartLinkID/NextLinkID without allowing malformed local data to
    /// recurse forever. Missing links remain explicit local presentation gaps.
    /// </summary>
    public Net7EffectGraphResolution ResolveEffectGraph(int rootDefinitionId, int maxDefinitions = 256)
    {
        if (maxDefinitions <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDefinitions));

        var resolved = new List<Net7EffectDefinition>();
        var missing = new SortedSet<int>();
        var visited = new HashSet<int>();
        var active = new HashSet<int>();
        bool cycle = false;
        bool limit = false;

        void Visit(int id)
        {
            if (resolved.Count >= maxDefinitions)
            {
                limit = true;
                return;
            }
            if (active.Contains(id))
            {
                cycle = true;
                return;
            }
            if (!visited.Add(id))
                return;
            if (!_effects.TryGetValue(id, out Net7EffectDefinition? definition))
            {
                missing.Add(id);
                return;
            }

            active.Add(id);
            resolved.Add(definition);
            // Current effect.ini uses -1 as the no-link sentinel. Zero is a
            // valid EFFECT-0 selector and must not be silently discarded.
            if (definition.StartLinkId is int start && start >= 0)
                Visit(start);
            if (definition.NextLinkId is int next && next >= 0)
                Visit(next);
            active.Remove(id);
        }

        Visit(rootDefinitionId);
        return new Net7EffectGraphResolution(rootDefinitionId, resolved, missing.ToArray(), cycle, limit);
    }

    private static Net7EffectDefinition BuildEffectDefinition(int id, IReadOnlyDictionary<string, string> values)
    {
        values.TryGetValue("EffectClass", out string? effectClass);
        effectClass ??= string.Empty;
        Net7EffectClassKind kind = ParseEffectClass(effectClass);
        values.TryGetValue("Duration", out string? durationRaw);
        // Current retail compares the token against the literal "INFINITE"
        // with a bytewise case-sensitive string comparison.
        bool infinite = string.Equals(durationRaw?.Trim(), "INFINITE", StringComparison.Ordinal);
        float? durationValue = !infinite && TryParseFloat(durationRaw, out float duration) ? duration : null;

        return new Net7EffectDefinition(
            id,
            effectClass,
            kind,
            TryReadInt(values, "BaseAssetID", out int baseAssetId) ? baseAssetId : null,
            ReadOptionalLinkSelector(values, "StartLinkID"),
            ReadOptionalLinkSelector(values, "NextLinkID"),
            durationRaw,
            infinite,
            durationValue,
            TryReadFloat(values, "StartDelay", out float startDelay) ? startDelay : null,
            TryReadFloat(values, "StartLinkDelay", out float startLinkDelay) ? startLinkDelay : null,
            TryReadFloat(values, "CycleTime", out float cycleTime) ? cycleTime : null,
            ReadBool(values, "UnaryStartLinkToTarget"),
            ReadBool(values, "UnaryNextLinkToTarget"),
            ReadBool(values, "TrackTarget"),
            ReadBool(values, "TrackSource"),
            ReadBool(values, "GoAwayWhenTargetDoes"),
            ReadBool(values, "GoAwayWhenSourceDoes"),
            ReadBool(values, "OffsetDurationPassedToStartLink"),
            ReadBool(values, "PassDurationToStartLink"),
            ReadBool(values, "DurationLinkedEffectUseNextLink"),
            ReadBool(values, "DurationLinkedEffectUseStartLink"),
            values.TryGetValue("SFX", out string? sfx) ? sfx : null,
            new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase));
    }

    private static Net7EffectClassKind ParseEffectClass(string value)
        => value.Trim() switch
        {
            "RObjLookAtEffect" => Net7EffectClassKind.RObjLookAtEffect,
            "RObjEffect" => Net7EffectClassKind.RObjEffect,
            "WarpEffect" => Net7EffectClassKind.WarpEffect,
            "LineEffect" => Net7EffectClassKind.LineEffect,
            "StraightSeekEffect" => Net7EffectClassKind.StraightSeekEffect,
            "SpiralSeekEffect" => Net7EffectClassKind.SpiralSeekEffect,
            "SplineRandomSeekEffect" => Net7EffectClassKind.SplineRandomSeekEffect,
            "LinearRandomSeekEffect" => Net7EffectClassKind.LinearRandomSeekEffect,
            "SoundEffect" => Net7EffectClassKind.SoundEffect,
            "PassEffect" => Net7EffectClassKind.PassEffect,
            "AttachEffect" => Net7EffectClassKind.AttachEffect,
            "RenderStateEffect" => Net7EffectClassKind.RenderStateEffect,
            "ParticleJetEffect" => Net7EffectClassKind.ParticleJetEffect,
            _ => Net7EffectClassKind.Unknown,
        };

    private static Dictionary<string, Dictionary<string, string>> ParseIni(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;

        using var reader = new StringReader(text);
        string? raw;
        while ((raw = reader.ReadLine()) is not null)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;
            if (line.Length >= 2 && line[0] == '[' && line[^1] == ']')
            {
                string sectionName = line[1..^1].Trim();
                if (!result.TryGetValue(sectionName, out current))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result.Add(sectionName, current);
                }
                continue;
            }
            if (current is null)
                continue;

            int equals = line.IndexOf('=');
            if (equals < 0)
            {
                current[line] = string.Empty;
                continue;
            }
            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();
            if (key.Length != 0)
                current[key] = value;
        }
        return result;
    }

    private static bool TryParseSectionId(string sectionName, string prefix, out int id)
    {
        id = 0;
        if (!sectionName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        return int.TryParse(sectionName.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out string? raw))
            return false;
        if (raw.Length == 0)
            return true;
        return raw.Trim() switch
        {
            "1" => true,
            "0" => false,
            var value when value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) => true,
            var value when value.Equals("YES", StringComparison.OrdinalIgnoreCase) => true,
            var value when value.Equals("ON", StringComparison.OrdinalIgnoreCase) => true,
            _ => false,
        };
    }

    private static bool TryReadInt(IReadOnlyDictionary<string, string> values, string key, out int value)
    {
        value = 0;
        return values.TryGetValue(key, out string? raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static int? ReadOptionalLinkSelector(IReadOnlyDictionary<string, string> values, string key)
    {
        // ConfigGetInteger uses -1 for absent/invalid link selectors in the
        // current client. Preserve other integers, including zero.
        return TryReadInt(values, key, out int value) && value != -1 ? value : null;
    }

    private static bool TryReadFloat(IReadOnlyDictionary<string, string> values, string key, out float value)
    {
        value = 0;
        return values.TryGetValue(key, out string? raw) && TryParseFloat(raw, out value);
    }

    private static bool TryParseFloat(string? raw, out float value)
    {
        value = 0;
        return raw is not null
            && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
