namespace EnbNext.Assets;

/// <summary>
/// Current retail rs.ini state TYPE tokens. Friendly class names are not
/// inferred beyond tokens verified in the current retail factory.
/// </summary>
public enum EnbRslStateType
{
    Unknown,
    Object,
    Animation,
    Sound,
    Link,
    Message,
    Bone,
    EffectPass,
}

/// <summary>Effective retail flags from the optional top-level [RSLID] section.
/// The library Properties retain the original authored values.</summary>
public sealed record EnbRslLibraryFlags(
    bool UseCulling,
    bool HintStationary,
    bool ShadowSource,
    bool ProjectionTarget,
    bool SimpleShadow);

/// <summary>Common RS_* flags read by the retail render-state base parser.</summary>
public sealed record EnbRslStateFlags(
    bool Delete,
    bool Deactivate,
    bool Stop,
    bool ActivateNext,
    bool Enabled,
    bool RemovePrototype,
    bool Precached,
    bool InstanceData,
    bool ActivateNextOnce,
    bool StopOnce,
    bool Stationary,
    bool StartsEnabled,
    bool DontStopSound,
    bool DontLoopSound);

/// <summary>
/// Optional per-state pivot mapping. Current installed rs.ini does not require
/// these for its ordinary corpus, but current retail supports sequential
/// PIVOTMAP%d sections and reads BONE/WEIGHT from them.
/// </summary>
public sealed record EnbRslPivotMap(
    int Ordinal,
    string? Bone,
    float? Weight,
    IReadOnlyDictionary<string, string> Properties);

/// <summary>
/// One authored N\STATEk entry from rs.ini. SectionOrdinal and StateId remain
/// separate because real retail data frequently assigns an ID that differs
/// from the sequential section ordinal (for example state ordinal 8 / ID 222).
/// If ID is absent, retail's state factory receives the section ordinal as its
/// construction fallback, so StateId is materialized from that ordinal while
/// StateIdWasExplicit records provenance.
/// </summary>
public sealed record EnbRslStateDefinition(
    int SectionOrdinal,
    int StateId,
    bool StateIdWasExplicit,
    EnbRslStateType Type,
    string TypeToken,
    string? File,
    string? Object,
    string? Animation,
    string? Sound,
    string? Suffix,
    int? NextStateId,
    int? LinkStateId,
    int? Message,
    float? Duration,
    string? InitString,
    EnbRslStateFlags Flags,
    IReadOnlyList<EnbRslPivotMap> PivotMaps,
    IReadOnlyDictionary<string, string> Properties);

/// <summary>One sequential RSL registry entry loaded from rs.ini.</summary>
public sealed class EnbRslLibraryDefinition
{
    private readonly Dictionary<int, EnbRslStateDefinition> _statesById;

    internal EnbRslLibraryDefinition(
        int rslId,
        EnbRslLibraryFlags flags,
        IReadOnlyList<EnbRslStateDefinition> states,
        IReadOnlyDictionary<string, string> properties)
    {
        RslId = rslId;
        Flags = flags;
        States = states;
        Properties = properties;
        _statesById = new Dictionary<int, EnbRslStateDefinition>();
        foreach (EnbRslStateDefinition state in states)
            _statesById[state.StateId] = state;
    }

    public int RslId { get; }

    public EnbRslLibraryFlags Flags { get; }

    public IReadOnlyList<EnbRslStateDefinition> States { get; }

    public IReadOnlyDictionary<string, string> Properties { get; }

    public bool TryFindState(int stateId, out EnbRslStateDefinition state)
        => _statesById.TryGetValue(stateId, out state!);
}

/// <summary>
/// Asset-native representation of current retail rs.ini.
///
/// LoadRenderSettingsIni enumerates libraries from 0 upward while N\STATE0
/// exists and stops at the first missing library. Each library likewise scans
/// STATE0, STATE1, ... and stops at the first missing state section.
/// </summary>
public sealed class EnbRetailRenderStateCatalog
{
    private readonly Dictionary<int, EnbRslLibraryDefinition> _libraries;

    private EnbRetailRenderStateCatalog(Dictionary<int, EnbRslLibraryDefinition> libraries)
    {
        _libraries = libraries;
        StateCount = libraries.Values.Sum(static library => library.States.Count);
    }

    public int LibraryCount => _libraries.Count;

    public int StateCount { get; }

    public static EnbRetailRenderStateCatalog? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !EnbNext.Installation.EnbPathCase.FileExists(path))
            return null;

        try
        {
            EnbRetailRenderStateCatalog catalog = Parse(EnbRetailIniDocument.Load(path));
            return catalog.LibraryCount == 0 ? null : catalog;
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return null;
        }
    }

    public static EnbRetailRenderStateCatalog ParseText(string text)
        => Parse(EnbRetailIniDocument.Parse(text));

    public static EnbRetailRenderStateCatalog Parse(EnbRetailIniDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var libraries = new Dictionary<int, EnbRslLibraryDefinition>();
        for (int rslId = 0; ; rslId++)
        {
            string firstStateSection = $"{rslId}\\STATE0";
            if (!document.TryGetSection(firstStateSection, out _))
                break;

            libraries[rslId] = ParseLibrary(document, rslId);
        }

        return new EnbRetailRenderStateCatalog(libraries);
    }

    public bool TryGetLibrary(int rslId, out EnbRslLibraryDefinition library)
        => _libraries.TryGetValue(rslId, out library!);

    private static EnbRslLibraryDefinition ParseLibrary(
        EnbRetailIniDocument document,
        int rslId)
    {
        IReadOnlyDictionary<string, string> rootProperties =
            document.TryGetSection(rslId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var root)
                ? root
                : EmptyProperties;

        // 0045BE40 ORs 0x28 for ShadowSource, 0x48 for ProjectionTarget,
        // and 0xA8 for SimpleShadow. These imply culling; SimpleShadow also
        // implies ShadowSource, which 00495430 requires for registration.
        bool simpleShadow = EnbRetailIniDocument.ReadBool(rootProperties, "RST_SIMPLE_SHADOW");
        bool shadowSource = EnbRetailIniDocument.ReadBool(rootProperties, "RST_SHADOW_SOURCE") || simpleShadow;
        bool projectionTarget = EnbRetailIniDocument.ReadBool(rootProperties, "RST_PROJECTION_TARGET");
        var flags = new EnbRslLibraryFlags(
            EnbRetailIniDocument.ReadBool(rootProperties, "RST_USE_CULLING") || shadowSource || projectionTarget,
            EnbRetailIniDocument.ReadBool(rootProperties, "RST_HINT_STATIONARY"),
            shadowSource,
            projectionTarget,
            simpleShadow);

        var states = new List<EnbRslStateDefinition>();
        for (int ordinal = 0; ; ordinal++)
        {
            string sectionName = $"{rslId}\\STATE{ordinal}";
            if (!document.TryGetSection(sectionName, out IReadOnlyDictionary<string, string> values))
                break;

            states.Add(ParseState(document, sectionName, ordinal, values));
        }

        return new EnbRslLibraryDefinition(rslId, flags, states, rootProperties);
    }

    private static EnbRslStateDefinition ParseState(
        EnbRetailIniDocument document,
        string sectionName,
        int ordinal,
        IReadOnlyDictionary<string, string> values)
    {
        bool explicitId = EnbRetailIniDocument.TryReadInt(values, "ID", out int parsedId);
        int stateId = explicitId ? parsedId : ordinal;

        values.TryGetValue("TYPE", out string? rawType);
        string typeToken = rawType?.Trim() ?? string.Empty;
        EnbRslStateType type = typeToken.ToLowerInvariant() switch
        {
            "object" => EnbRslStateType.Object,
            "anim" => EnbRslStateType.Animation,
            "sound" => EnbRslStateType.Sound,
            "link" => EnbRslStateType.Link,
            "message" => EnbRslStateType.Message,
            "bone" => EnbRslStateType.Bone,
            "effect_pass" => EnbRslStateType.EffectPass,
            _ => EnbRslStateType.Unknown,
        };

        string? suffix = ReadOptionalString(values, "SUFFIX");
        int? next = ReadOptionalInt(values, "NEXT", normalizeMinusOne: true);
        int? link = ReadOptionalInt(values, "LINK", normalizeMinusOne: true);
        int? message = ReadOptionalInt(values, "MESSAGE");
        float? duration = ReadOptionalFloat(values, "DURATION");

        var flags = new EnbRslStateFlags(
            EnbRetailIniDocument.ReadBool(values, "RS_DELETE"),
            EnbRetailIniDocument.ReadBool(values, "RS_DEACTIVATE"),
            EnbRetailIniDocument.ReadBool(values, "RS_STOP"),
            EnbRetailIniDocument.ReadBool(values, "RS_ACTIVATE_NEXT"),
            EnbRetailIniDocument.ReadBool(values, "RS_ENABLED"),
            EnbRetailIniDocument.ReadBool(values, "RS_REMOVE_PROTOTYPE"),
            EnbRetailIniDocument.ReadBool(values, "RS_PRECACHED"),
            EnbRetailIniDocument.ReadBool(values, "RS_INSTANCE_DATA"),
            EnbRetailIniDocument.ReadBool(values, "RS_ACTIVATE_NEXT_ONCE"),
            EnbRetailIniDocument.ReadBool(values, "RS_STOP_ONCE"),
            EnbRetailIniDocument.ReadBool(values, "RS_STATIONARY"),
            EnbRetailIniDocument.ReadBool(values, "RS_STARTS_ENABLED"),
            EnbRetailIniDocument.ReadBool(values, "RS_DONT_STOP_SOUND"),
            EnbRetailIniDocument.ReadBool(values, "RS_DONT_LOOP_SOUND"));

        return new EnbRslStateDefinition(
            ordinal,
            stateId,
            explicitId,
            type,
            typeToken,
            ReadOptionalString(values, "FILE"),
            ReadOptionalString(values, "OBJECT"),
            ReadOptionalString(values, "ANIM"),
            ReadOptionalString(values, "SOUND"),
            suffix,
            next,
            link,
            message,
            duration,
            ReadOptionalString(values, "INITSTR"),
            flags,
            ParsePivotMaps(document, sectionName),
            values);
    }

    private static IReadOnlyList<EnbRslPivotMap> ParsePivotMaps(
        EnbRetailIniDocument document,
        string stateSectionName)
    {
        var pivotMaps = new List<EnbRslPivotMap>();
        for (int ordinal = 0; ; ordinal++)
        {
            string sectionName = $"{stateSectionName}\\PIVOTMAP{ordinal}";
            if (!document.TryGetSection(sectionName, out IReadOnlyDictionary<string, string> values))
                break;

            pivotMaps.Add(new EnbRslPivotMap(
                ordinal,
                ReadOptionalString(values, "BONE"),
                ReadOptionalFloat(values, "WEIGHT"),
                values));
        }

        return pivotMaps;
    }

    private static string? ReadOptionalString(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        if (!values.TryGetValue(key, out string? raw))
            return null;

        string trimmed = raw.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static int? ReadOptionalInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        bool normalizeMinusOne = false)
    {
        if (!EnbRetailIniDocument.TryReadInt(values, key, out int value))
            return null;
        return normalizeMinusOne && value == -1 ? null : value;
    }

    private static float? ReadOptionalFloat(
        IReadOnlyDictionary<string, string> values,
        string key)
        => EnbRetailIniDocument.TryReadFloat(values, key, out float value) && float.IsFinite(value)
            ? value
            : null;

    private static readonly IReadOnlyDictionary<string, string> EmptyProperties =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Neutral presentation commands for the four proven retail render-state entry
/// points. These are asset/presentation descriptors and deliberately have no
/// dependency on Net7 packet classes.
/// </summary>
public enum EnbRslRenderStateCommandKind
{
    InitializeState,
    ActivateState,
    ActivateNextStateOnce,
    DeactivateState,
}

public sealed record EnbRslRenderStateCommand(
    EnbRslRenderStateCommandKind Kind,
    int StateId,
    int? ArgumentStateId = null);

public static class EnbRslRenderStateSemantics
{
    // All six class vtables dispatch +8 to00458590 RET8. Link alone
    // dispatches0045A1D0, which writes its retained LINK ID/cache.
    public static bool IsNoOpInitializer(EnbRslStateType type)=>type is EnbRslStateType.Object or
        EnbRslStateType.Animation or EnbRslStateType.Sound or EnbRslStateType.Bone or
        EnbRslStateType.Message or EnbRslStateType.EffectPass;

    /// <summary>
    /// Reproduces CreateBaseAssetRenderObjectFromConfig's RSL initialization:
    /// without LINKID retail calls ActivateState(RSID); with LINKID it first
    /// initializes LINKID with RSID, then activates LINKID. Missing RSID reaches
    /// the retail state method as -1 and is kept explicit here.
    /// </summary>
    public static IReadOnlyList<EnbRslRenderStateCommand> BuildInitialCommands(
        int? linkId,
        int? rsId)
    {
        int initialStateId = rsId ?? -1;
        if (linkId is not int link || link == -1)
        {
            return
            [
                new EnbRslRenderStateCommand(
                    EnbRslRenderStateCommandKind.ActivateState,
                    initialStateId),
            ];
        }

        return
        [
            new EnbRslRenderStateCommand(
                EnbRslRenderStateCommandKind.InitializeState,
                link,
                initialStateId),
            new EnbRslRenderStateCommand(
                EnbRslRenderStateCommandKind.ActivateState,
                link),
        ];
    }
}
