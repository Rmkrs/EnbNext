namespace EnbNext.Assets;

/// <summary>Retail basset.ini resolver branches established by the static asset work.</summary>
public enum EnbBaseAssetDefinitionKind
{
    Missing,
    NullObject,
    Rsl,
    FileName,
    ProceduralWidth,
    Unsupported,
    NoRenderObject,
}

public sealed record EnbBaseAssetDefinition(
    int Selector,
    EnbBaseAssetDefinitionKind Kind,
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
    public bool IsMissing => Kind == EnbBaseAssetDefinitionKind.Missing;
}

/// <summary>The thirteen exact current retail EffectClass strings.</summary>
public enum EnbEffectClassKind
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

/// <summary>
/// Current-retail RenderStateEffect ActionType enumeration. The numeric values
/// are the exact indices returned by the current client's case-insensitive INI
/// enum reader for {Activate, Deactivate, Linger, Ramp, ActivateNextStateOnce}.
/// </summary>
public enum EnbRenderStateEffectActionKind
{
    Unknown = -1,
    Activate = 0,
    Deactivate = 1,
    Linger = 2,
    Ramp = 3,
    ActivateNextStateOnce = 4,
}

public sealed record EnbEffectDefinition(
    int DefinitionId,
    string EffectClass,
    EnbEffectClassKind EffectClassKind,
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
    IReadOnlyDictionary<string, string> Properties,
    int? RenderStateId = null,
    EnbRenderStateEffectActionKind RenderStateAction = EnbRenderStateEffectActionKind.Unknown,
    int? RenderStateActionTimeMilliseconds = null,
    bool RenderStateForceReactivate = false)
{
    public bool UsesBaseAssetDirectly => EffectClassKind is
        EnbEffectClassKind.RObjLookAtEffect or
        EnbEffectClassKind.RObjEffect or
        EnbEffectClassKind.WarpEffect or
        EnbEffectClassKind.LineEffect or
        EnbEffectClassKind.StraightSeekEffect or
        EnbEffectClassKind.SpiralSeekEffect or
        EnbEffectClassKind.SplineRandomSeekEffect or
        EnbEffectClassKind.LinearRandomSeekEffect or
        EnbEffectClassKind.AttachEffect or
        EnbEffectClassKind.ParticleJetEffect;
}

public sealed record EnbEffectPresentationResolution(
    int DefinitionId,
    EnbEffectDefinition? Definition,
    EnbBaseAssetDefinition? BaseAsset,
    bool MissingDefinition,
    bool InvalidDefinition,
    bool MissingBaseAsset);

public sealed record EnbEffectGraphResolution(
    int RootDefinitionId,
    IReadOnlyList<EnbEffectDefinition> Definitions,
    IReadOnlyList<int> MissingDefinitionIds,
    bool CycleDetected,
    bool TraversalLimitReached);

/// <summary>
/// Asset-native basset.ini/effect.ini catalog. This type deliberately has no
/// dependency on Net7 packets or world state. The older Net7 presentation
/// catalog remains temporarily in place until moves domain-to-presentation
/// resolution behind the immutable snapshot boundary.
/// </summary>
public sealed class EnbRetailPresentationCatalog
{
    private readonly Dictionary<int, IReadOnlyDictionary<string, string>> _baseSections = [];
    private readonly Dictionary<int, EnbEffectDefinition> _effects = [];

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
        LoadBaseAssetsDocument(EnbRetailIniDocument.Load(path));
    }

    public void LoadEffects(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        LoadEffectsDocument(EnbRetailIniDocument.Load(path));
    }

    public void LoadBaseAssetsText(string text)
        => LoadBaseAssetsDocument(EnbRetailIniDocument.Parse(text));

    public void LoadEffectsText(string text)
        => LoadEffectsDocument(EnbRetailIniDocument.Parse(text));

    public void LoadBaseAssetsDocument(EnbRetailIniDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        _baseSections.Clear();
        foreach ((string name, IReadOnlyDictionary<string, string> values) in document.EnumerateSections())
        {
            if (EnbRetailIniDocument.TryParseSectionId(name, "BASE-", out int id))
                _baseSections[id] = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void LoadEffectsDocument(EnbRetailIniDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        _effects.Clear();
        foreach ((string name, IReadOnlyDictionary<string, string> values) in document.EnumerateSections())
        {
            if (!EnbRetailIniDocument.TryParseSectionId(name, "EFFECT-", out int id))
                continue;

            _effects[id] = BuildEffectDefinition(id, values);
        }
    }

    public EnbBaseAssetDefinition ResolveBaseAsset(int selector)
    {
        // Current retail treats -1 as no local presentation.
        if (selector == -1)
            return new EnbBaseAssetDefinition(selector, EnbBaseAssetDefinitionKind.NullObject, IsCatalogEntry: false);

        if (!_baseSections.TryGetValue(selector, out IReadOnlyDictionary<string, string>? values))
            return new EnbBaseAssetDefinition(selector, EnbBaseAssetDefinitionKind.Missing, IsCatalogEntry: false);

        bool overbrightOff = EnbRetailIniDocument.ReadBool(values, "OverbrightOff");
        if (values.ContainsKey("NULLOBJECT"))
        {
            return new EnbBaseAssetDefinition(
                selector,
                EnbBaseAssetDefinitionKind.NullObject,
                true,
                OverbrightOff: overbrightOff,
                Properties: values);
        }

        if (values.ContainsKey("RSLID"))
        {
            // Retail commits to the RSL branch on property presence. Invalid
            // RSLID does not fall through to FILE/NAME or Width.
            int rslId=EnbRetailIniDocument.ReadCurrentRetailInteger(values,"RSLID",-1);
            if (rslId == -1)
            {
                return new EnbBaseAssetDefinition(
                    selector,
                    EnbBaseAssetDefinitionKind.Missing,
                    true,
                    OverbrightOff: overbrightOff,
                    Properties: values);
            }

            int parsedLinkId=EnbRetailIniDocument.ReadCurrentRetailInteger(values,"LINKID",-1);
            int parsedRsId=EnbRetailIniDocument.ReadCurrentRetailInteger(values,"RSID",-1);
            int? linkId = parsedLinkId != -1
                ? parsedLinkId
                : null;
            int? rsId = parsedRsId != -1
                ? parsedRsId
                : null;

            return new EnbBaseAssetDefinition(
                selector,
                EnbBaseAssetDefinitionKind.Rsl,
                true,
                RslId: rslId,
                LinkId: linkId,
                RsId: rsId,
                OverbrightOff: overbrightOff,
                Properties: values);
        }

        //006F54AE commits on FILE presence;009CAEA0 supplies the empty
        //default for an absent NAME before native name/FILE/retry acquisition.
        if (values.TryGetValue("FILE", out string? file))
        {
            return new EnbBaseAssetDefinition(
                selector,
                EnbBaseAssetDefinitionKind.FileName,
                true,
                File: file,
                Name: values.GetValueOrDefault("NAME",string.Empty),
                OverbrightOff: overbrightOff,
                Properties: values);
        }

        if (EnbRetailIniDocument.TryReadFloat(values, "Width", out float width))
        {
            return new EnbBaseAssetDefinition(
                selector,
                EnbBaseAssetDefinitionKind.ProceduralWidth,
                true,
                Width: width,
                OverbrightOff: overbrightOff,
                Properties: values);
        }

        return new EnbBaseAssetDefinition(
            selector,
            values.ContainsKey("Width") ? EnbBaseAssetDefinitionKind.Unsupported : EnbBaseAssetDefinitionKind.NoRenderObject,
            true,
            OverbrightOff: overbrightOff,
            Properties: values);
    }

    public EnbEffectDefinition? FindEffect(int definitionId)
        => _effects.TryGetValue(definitionId, out EnbEffectDefinition? definition) ? definition : null;

    public EnbEffectPresentationResolution ResolveEffect(int definitionId)
    {
        if (!_effects.TryGetValue(definitionId, out EnbEffectDefinition? definition))
        {
            return new EnbEffectPresentationResolution(
                definitionId,
                null,
                null,
                MissingDefinition: true,
                InvalidDefinition: false,
                MissingBaseAsset: false);
        }

        if (definition.EffectClassKind == EnbEffectClassKind.Unknown)
        {
            return new EnbEffectPresentationResolution(
                definitionId,
                definition,
                null,
                MissingDefinition: false,
                InvalidDefinition: true,
                MissingBaseAsset: false);
        }

        EnbBaseAssetDefinition? baseAsset = null;
        bool missingBaseAsset = false;
        if (definition.UsesBaseAssetDirectly && definition.BaseAssetId is int baseAssetId)
        {
            baseAsset = ResolveBaseAsset(baseAssetId);
            missingBaseAsset = baseAsset.IsMissing;
        }

        return new EnbEffectPresentationResolution(
            definitionId,
            definition,
            baseAsset,
            MissingDefinition: false,
            InvalidDefinition: false,
            MissingBaseAsset: missingBaseAsset);
    }

    public EnbEffectGraphResolution ResolveEffectGraph(int rootDefinitionId, int maxDefinitions = 256)
    {
        if (maxDefinitions <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDefinitions));

        var resolved = new List<EnbEffectDefinition>();
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

            if (!_effects.TryGetValue(id, out EnbEffectDefinition? definition))
            {
                missing.Add(id);
                return;
            }

            active.Add(id);
            resolved.Add(definition);

            // effect.ini uses -1 as the no-link sentinel; zero is valid.
            if (definition.StartLinkId is int start && start >= 0)
                Visit(start);
            if (definition.NextLinkId is int next && next >= 0)
                Visit(next);

            active.Remove(id);
        }

        Visit(rootDefinitionId);
        return new EnbEffectGraphResolution(rootDefinitionId, resolved, missing.ToArray(), cycle, limit);
    }

    private static EnbEffectDefinition BuildEffectDefinition(
        int id,
        IReadOnlyDictionary<string, string> values)
    {
        values.TryGetValue("EffectClass", out string? effectClass);
        effectClass ??= string.Empty;
        EnbEffectClassKind kind = ParseEffectClass(effectClass);

        values.TryGetValue("Duration", out string? durationRaw);
        bool infinite = string.Equals(durationRaw?.Trim(), "INFINITE", StringComparison.Ordinal);
        float? durationValue = !infinite && EnbRetailIniDocument.TryParseFloat(durationRaw, out float duration)
            ? duration
            : null;

        int? renderStateId = null;
        EnbRenderStateEffectActionKind renderStateAction = EnbRenderStateEffectActionKind.Unknown;
        int? renderStateActionTimeMilliseconds = null;
        bool renderStateForceReactivate = false;
        if (kind == EnbEffectClassKind.RenderStateEffect)
        {
            renderStateId = EnbRetailIniDocument.TryReadInt(values, "RenderStateID", out int parsedStateId)
                ? parsedStateId
                : -1;
            renderStateAction = ParseRenderStateEffectAction(
                values.TryGetValue("ActionType", out string? actionType) ? actionType : null);
            float actionTimeSeconds = EnbRetailIniDocument.TryReadFloat(values, "ActionTime", out float parsedActionTime)
                ? parsedActionTime
                : 1.0f;
            renderStateActionTimeMilliseconds = ToCurrentRetailMilliseconds(actionTimeSeconds);
            renderStateForceReactivate = EnbRetailIniDocument.ReadBool(values, "ForceReactivate");
        }

        return new EnbEffectDefinition(
            id,
            effectClass,
            kind,
            EnbRetailIniDocument.TryReadInt(values, "BaseAssetID", out int baseAssetId) ? baseAssetId : null,
            ReadOptionalLinkSelector(values, "StartLinkID"),
            ReadOptionalLinkSelector(values, "NextLinkID"),
            durationRaw,
            infinite,
            durationValue,
            EnbRetailIniDocument.TryReadFloat(values, "StartDelay", out float startDelay) ? startDelay : null,
            EnbRetailIniDocument.TryReadFloat(values, "StartLinkDelay", out float startLinkDelay) ? startLinkDelay : null,
            EnbRetailIniDocument.TryReadFloat(values, "CycleTime", out float cycleTime) ? cycleTime : null,
            EnbRetailIniDocument.ReadBool(values, "UnaryStartLinkToTarget"),
            EnbRetailIniDocument.ReadBool(values, "UnaryNextLinkToTarget"),
            EnbRetailIniDocument.ReadBool(values, "TrackTarget"),
            EnbRetailIniDocument.ReadBool(values, "TrackSource"),
            EnbRetailIniDocument.ReadBool(values, "GoAwayWhenTargetDoes"),
            EnbRetailIniDocument.ReadBool(values, "GoAwayWhenSourceDoes"),
            EnbRetailIniDocument.ReadBool(values, "OffsetDurationPassedToStartLink"),
            EnbRetailIniDocument.ReadBool(values, "PassDurationToStartLink"),
            EnbRetailIniDocument.ReadBool(values, "DurationLinkedEffectUseNextLink"),
            EnbRetailIniDocument.ReadBool(values, "DurationLinkedEffectUseStartLink"),
            values.TryGetValue("SFX", out string? sfx) ? sfx : null,
            new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase),
            renderStateId,
            renderStateAction,
            renderStateActionTimeMilliseconds,
            renderStateForceReactivate);
    }

    private static EnbRenderStateEffectActionKind ParseRenderStateEffectAction(string? value)
        => value?.Trim() switch
        {
            string action when action.Equals("Activate", StringComparison.OrdinalIgnoreCase) => EnbRenderStateEffectActionKind.Activate,
            string action when action.Equals("Deactivate", StringComparison.OrdinalIgnoreCase) => EnbRenderStateEffectActionKind.Deactivate,
            string action when action.Equals("Linger", StringComparison.OrdinalIgnoreCase) => EnbRenderStateEffectActionKind.Linger,
            string action when action.Equals("Ramp", StringComparison.OrdinalIgnoreCase) => EnbRenderStateEffectActionKind.Ramp,
            string action when action.Equals("ActivateNextStateOnce", StringComparison.OrdinalIgnoreCase) => EnbRenderStateEffectActionKind.ActivateNextStateOnce,
            _ => EnbRenderStateEffectActionKind.Unknown,
        };

    private static int ToCurrentRetailMilliseconds(float seconds)
    {
        // Current client loads a 32-bit float, multiplies it by 1000.0f in the
        // x87 stack, then calls its __ftol helper, which temporarily selects
        // truncate-toward-zero before FISTP. The RenderStateEffect constructor
        // stores that integer millisecond result back as a float.
        double milliseconds = (double)seconds * 1000.0;
        if (!double.IsFinite(milliseconds) || milliseconds > int.MaxValue || milliseconds < int.MinValue)
            throw new InvalidDataException($"RenderStateEffect ActionTime '{seconds}' is outside the current-retail millisecond range.");
        return (int)milliseconds;
    }

    private static int? ReadOptionalLinkSelector(
        IReadOnlyDictionary<string, string> values,
        string key)
        => EnbRetailIniDocument.TryReadInt(values, key, out int value) && value != -1
            ? value
            : null;

    private static EnbEffectClassKind ParseEffectClass(string value)
        => value.Trim() switch
        {
            "RObjLookAtEffect" => EnbEffectClassKind.RObjLookAtEffect,
            "RObjEffect" => EnbEffectClassKind.RObjEffect,
            "WarpEffect" => EnbEffectClassKind.WarpEffect,
            "LineEffect" => EnbEffectClassKind.LineEffect,
            "StraightSeekEffect" => EnbEffectClassKind.StraightSeekEffect,
            "SpiralSeekEffect" => EnbEffectClassKind.SpiralSeekEffect,
            "SplineRandomSeekEffect" => EnbEffectClassKind.SplineRandomSeekEffect,
            "LinearRandomSeekEffect" => EnbEffectClassKind.LinearRandomSeekEffect,
            "SoundEffect" => EnbEffectClassKind.SoundEffect,
            "PassEffect" => EnbEffectClassKind.PassEffect,
            "AttachEffect" => EnbEffectClassKind.AttachEffect,
            "RenderStateEffect" => EnbEffectClassKind.RenderStateEffect,
            "ParticleJetEffect" => EnbEffectClassKind.ParticleJetEffect,
            _ => EnbEffectClassKind.Unknown,
        };
}
