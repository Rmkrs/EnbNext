using System.Buffers.Binary;
using System.Globalization;

namespace EnbNext.Assets;

/// <summary>
/// Presentation/runtime values loaded from retail's per-player
/// Data/client/output/player&lt;id&gt;_options.ini file. Numeric graphics values are
/// nullable because current retail first creates them from its graphics-settings
/// object, then overlays only persisted values that are actually present.
/// </summary>
public sealed record EnbRetailPlayerOptions(
    bool BridgeMode,
    bool GraphicsDefault = true,
    float? GraphicsGamma = null,
    float? GraphicsLandPoly = null,
    float? GraphicsLandDetail = null,
    float? GraphicsSpacePoly = null,
    float? GraphicsTextureSize = null,
    float RadarGutterOpacity = 0.75f,
    bool CameraGate = false,
    float InterfaceTooltipDelay = 50f,
    bool CpitThrottleRightMouse = true,
    bool CpitThrottleClickLocks = true,
    float SoundMusic = 30f,
    float SoundSfx = 80f,
    bool DopplerEnabled = true,
    float StarmapMinFrame = 29f,
    float StarmapMaxFrame = 59f,
    bool StarmapMaximized = true,
    int StarmapLabel = 1,
    int StarmapShowCoords = 0,
    bool CameraWarp = true,
    bool CameraShip = true,
    bool CameraLoot = true,
    bool CameraLand = true,
    bool CameraDock = true,
    bool CameraBridge = true,
    bool StarbaseDefaultRun = true,
    bool RestrictedStatus = false)
{
    public bool AutopilotEnabled { get; init; }
    public float SoundDialog { get; init; } = 100f;
    public bool StarbaseThirdPersonMode { get; init; } = true;
    public IReadOnlyDictionary<string, string> FontOptions { get; init; } = new Dictionary<string, string>();
    public bool GalaxyMapShowAll { get; init; }
    public int InventoryCargoSort { get; init; }
    public int InventoryVaultSort { get; init; }
    public bool InventoryCargoReverse { get; init; }
    public bool InventoryVaultReverse { get; init; }
    public IReadOnlyList<EnbRetailInventorySortPreset> InventorySortPresets { get; init; } = EnbRetailInventorySortPreset.Defaults;
    public EnbRetailInventorySortPreset InventorySort(int mode) => mode>=0 && mode<InventorySortPresets.Count ? InventorySortPresets[mode] : new("",0,0,0);
    public bool TradeSortByName { get; init; }
    public string InventoryCargoFilter { get; init; } = "All";
    public string InventoryVaultFilter { get; init; } = "All";
    public System.Collections.Immutable.ImmutableArray<string> MonitoredChatChannels { get; init; } = ["New Players"];
}

public sealed record EnbRetailGraphicsOptions(
    bool UseRecommendedValues,
    float Gamma,
    float LandPoly,
    float LandDetail,
    float SpacePoly,
    float TextureSize)
{
    public EnbRetailGraphicsOptions Clamp() => this with
    {
        Gamma = Math.Clamp(Gamma, 0f, 100f),
        LandPoly = Math.Clamp(LandPoly, 0f, 100f),
        LandDetail = Math.Clamp(LandDetail, 0f, 100f),
        SpacePoly = Math.Clamp(SpacePoly, 0f, 100f),
        TextureSize = Math.Clamp(TextureSize, 0f, 100f),
    };
}

/// <summary>
/// Current retail player-options parser for the bounded values EnbNext needs.
/// Retail constructs player%d_options.ini from the retained SectorJoin 64-bit
/// selector modulo 1,000,000,000. On the current Net7Proxy success path that
/// retained value has bswap32(fixedSessionId) in its low dword and zero in its
/// high dword.
/// </summary>
public static class EnbRetailPlayerOptionsCatalog
{
    private const ulong PlayerOptionsSelectorModulus = 1_000_000_000UL;
    private const string BooleanSectionPrefix = "BOOL-";
    private const string FloatSectionPrefix = "FLOAT-";
    private const string OptionNameKey = "OptionName";
    private const string OptionValueKey = "OptionValue";
    private const string BridgeModeOptionName = "Bridge_Mode";
    private const string CameraGateOptionName = "Camera_Gate";
    private const string GraphicsDefaultOptionName = "Graphics_Default";
    private const string GraphicsGammaOptionName = "Graphics_Gamma";
    private const string GraphicsLandPolyOptionName = "Graphics_Land_Poly";
    private const string GraphicsLandDetailOptionName = "Graphics_Land_Detail";
    private const string GraphicsSpacePolyOptionName = "Graphics_Space_Poly";
    private const string GraphicsTextureSizeOptionName = "Graphics_Texture_Size";
    private const string RadarGutterOpacityOptionName = "Radar_Gutter_Opacity";
    // Current retail named-channel table 00BA8C20; 00749ED0 corroborates option mappings.
    // Startup needs "Out of Context", not the separate /ooc producer's shorthand.
    private static readonly (string Option, string Channel)[] NamedChannels =
    [
        ("Channel_General", "General"), ("Channel_OOC", "Out of Context"), ("Channel_Market", "Market"),
        ("Channel_Newbie", "New Players"), ("Channel_Terran", "Terran"), ("Channel_Jenquai", "Jenquai"),
        ("Channel_Progen", "Progen"), ("Channel_Explorers", "Explorers"), ("Channel_Warriors", "Warriors"),
        ("Channel_Traders", "Tradesmen"), ("Channel_Sentinels", "Sentinels"),
        ("Channel_Defenders", "Defenders"), ("Channel_Enforcers", "Enforcers"),
    ];

    public static EnbRetailPlayerOptions Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(EnbRetailIniDocument.Load(path));
    }

    public static EnbRetailPlayerOptions ParseText(string text)
        => Parse(EnbRetailIniDocument.Parse(text));

    public static EnbRetailPlayerOptions Parse(EnbRetailIniDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // ConstructOptionsManager defines Bridge_Mode=false and
        // Graphics_Default=true before persisted values are applied. Numeric
        // defaults are computed from the current graphics-settings object, so
        // this parser preserves absence rather than manufacturing a captured
        // machine value.
        bool bridgeMode = false;
        // 007F415C/60: both current and default Starbase_Default_Run are true.
        bool starbaseDefaultRun = true;
        // 007F45AA/AE initializes current and default to true.
        bool starbaseThirdPersonMode = true;
        // ConstructOptionsManager 007F423A/3D initializes both bytes to zero.
        bool restrictedStatus = false;
        // 007F4766/69: Galaxymap_Show_All current/default bytes are zero.
        bool galaxyMapShowAll = false;
        // 007F43F2/43F5: current/default Trade_Sort_By_Name are false.
        bool tradeSortByName = false;
        int cargoSort=0,vaultSort=0;
        bool cargoReverse=false,vaultReverse=false;
        var sortPresets=EnbRetailInventorySortPreset.Defaults.ToArray();
        // 7F6959/7F69C7: independent string options, both default to All.
        string cargoFilter = "All", vaultFilter = "All";
        // 007F1AD0: only New Players starts enabled among named channels.
        var monitoredChannels = new HashSet<string>(StringComparer.Ordinal) { "New Players" };
        bool cameraGate = false;
        // 007F361C/20: current/default Camera_Warp bytes are both true.
        bool cameraWarp = true;
        bool cameraShip = true, cameraLoot = true, cameraLand = true, cameraDock = true, cameraBridge = true;
        // ConstructOptionsManager @ 007F407E/82 sets current/default to true.
        bool throttleRightMouse = true;
        // ConstructOptionsManager: both current/default ClickLocks bytes are 1.
        bool throttleClickLocks = true;
        bool graphicsDefault = true;
        // ConstructOptionsManager @ 007F3C22: current/default bytes are both 1.
        bool dopplerEnabled = true;
        bool autopilotEnabled = false;
        float? graphicsGamma = null;
        float? graphicsLandPoly = null;
        float? graphicsLandDetail = null;
        float? graphicsSpacePoly = null;
        float? graphicsTextureSize = null;
        // ConstructFloatOption in current client.exe supplies 0.75 before any
        // persisted player option is overlaid.
        float radarGutterOpacity = 0.75f;
        // ConstructOptionsManager @ 007F4D73: PUSH 42480000 (50.0f).
        float tooltipDelay = 50f;
        // ConstructOptionsManager: 41F00000 / 42A00000; persisted 0 is mute.
        float soundMusic = 30f, soundSfx = 80f, soundDialog = 100f;
        var fontOptions = new Dictionary<string, string>(StringComparer.Ordinal);
        float mapMin = 29f, mapMax = 59f;
        bool mapMaximized = true;
        int mapLabel = 1, mapCoordinates = 0;

        foreach ((string sectionName, IReadOnlyDictionary<string, string> values) in document.EnumerateSections())
        {
            if (!values.TryGetValue(OptionNameKey, out string? optionName))
                continue;

            if (sectionName.StartsWith("STRING-", StringComparison.OrdinalIgnoreCase) && optionName.StartsWith("FONT_", StringComparison.Ordinal) && values.TryGetValue(OptionValueKey, out var fontValue)) fontOptions[optionName] = fontValue;

            if (sectionName.StartsWith("STRING-", StringComparison.OrdinalIgnoreCase) && values.TryGetValue(OptionValueKey, out var filterValue))
            {
                for(int i=0;i<sortPresets.Length;i++)
                    if(optionName.Equals($"Inv_Sort{i}_Name",StringComparison.OrdinalIgnoreCase))sortPresets[i]=sortPresets[i] with {Name=filterValue};
                if (optionName.Equals("Inv_Filter_Cargo", StringComparison.OrdinalIgnoreCase)) cargoFilter = filterValue;
                else if (optionName.Equals("Inv_Filter_Vault", StringComparison.OrdinalIgnoreCase)) vaultFilter = filterValue;
            }

            if (sectionName.StartsWith(BooleanSectionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (!values.TryGetValue(OptionValueKey, out string? rawBoolean))
                    continue;

                foreach (var channel in NamedChannels)
                    if (optionName.Equals(channel.Option, StringComparison.OrdinalIgnoreCase))
                    {
                        if (ReadPersistedBoolean(rawBoolean)) monitoredChannels.Add(channel.Channel);
                        else monitoredChannels.Remove(channel.Channel);
                        break;
                    }

                if(optionName.Equals("Inv_Sort_Cargo_Reverse",StringComparison.OrdinalIgnoreCase))cargoReverse=ReadPersistedBoolean(rawBoolean);
                if(optionName.Equals("Inv_Sort_Vault_Reverse",StringComparison.OrdinalIgnoreCase))vaultReverse=ReadPersistedBoolean(rawBoolean);
                if (optionName.Equals(BridgeModeOptionName, StringComparison.OrdinalIgnoreCase))
                    bridgeMode = ReadPersistedBoolean(rawBoolean);
                else if (optionName.Equals("Starbase_Default_Run", StringComparison.OrdinalIgnoreCase))
                    starbaseDefaultRun = ReadPersistedBoolean(rawBoolean);
                else if (optionName.Equals("Starbase_Third_Person_Mode", StringComparison.OrdinalIgnoreCase))
                    starbaseThirdPersonMode = ReadPersistedBoolean(rawBoolean);
                else if (optionName.Equals("RestrictedStatus", StringComparison.OrdinalIgnoreCase))
                    restrictedStatus = ReadPersistedBoolean(rawBoolean);
                else if (optionName.Equals("Galaxymap_Show_All", StringComparison.OrdinalIgnoreCase))
                    galaxyMapShowAll = ReadPersistedBoolean(rawBoolean);
                else if (optionName.Equals("Trade_Sort_By_Name", StringComparison.OrdinalIgnoreCase))
                    tradeSortByName = ReadPersistedBoolean(rawBoolean);
                else if (optionName.Equals(CameraGateOptionName, StringComparison.OrdinalIgnoreCase))
                    cameraGate = ReadPersistedBoolean(rawBoolean);
                else if (optionName.Equals("Camera_Warp", StringComparison.OrdinalIgnoreCase))
                    cameraWarp = ReadPersistedBoolean(rawBoolean);
                else if (optionName.Equals("Camera_Ship", StringComparison.OrdinalIgnoreCase)) cameraShip = ReadPersistedBoolean(rawBoolean);
                else if (optionName.Equals("Camera_Loot", StringComparison.OrdinalIgnoreCase)) cameraLoot = ReadPersistedBoolean(rawBoolean);
                else if (optionName.Equals("Camera_Land", StringComparison.OrdinalIgnoreCase)) cameraLand = ReadPersistedBoolean(rawBoolean);
                else if (optionName.Equals("Camera_Dock", StringComparison.OrdinalIgnoreCase)) cameraDock = ReadPersistedBoolean(rawBoolean);
                else if (optionName.Equals("Camera_Bridge", StringComparison.OrdinalIgnoreCase)) cameraBridge = ReadPersistedBoolean(rawBoolean);
                else if (optionName.Equals(GraphicsDefaultOptionName, StringComparison.OrdinalIgnoreCase))
                    graphicsDefault = ReadPersistedBoolean(rawBoolean);
                else if (optionName.Equals("Cpit_ThrottleRightMouse", StringComparison.OrdinalIgnoreCase))
                    throttleRightMouse = ReadPersistedBoolean(rawBoolean);
                else if (optionName.Equals("Cpit_ThrottleClickLocks", StringComparison.OrdinalIgnoreCase))
                    throttleClickLocks = ReadPersistedBoolean(rawBoolean);
                else if (optionName.Equals("Doppler_Enabled", StringComparison.OrdinalIgnoreCase))
                    dopplerEnabled = ReadPersistedBoolean(rawBoolean);
                else if (optionName.Equals("Next_Autopilot", StringComparison.OrdinalIgnoreCase))
                    autopilotEnabled = ReadPersistedBoolean(rawBoolean);
                else if (optionName.Equals("Starmap_Maximized", StringComparison.OrdinalIgnoreCase))
                    mapMaximized = ReadPersistedBoolean(rawBoolean);

                continue;
            }

            if (sectionName.StartsWith("INT-", StringComparison.OrdinalIgnoreCase))
            {
                if (values.TryGetValue(OptionValueKey, out string? rawInteger) &&
                    int.TryParse(rawInteger, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer))
                {
                    // Construction reads the low byte through 7F9610, not a boolean conversion.
                    if(optionName.Equals("Inv_Sort_Cargo",StringComparison.OrdinalIgnoreCase))cargoSort=unchecked((byte)integer);
                    if(optionName.Equals("Inv_Sort_Vault",StringComparison.OrdinalIgnoreCase))vaultSort=unchecked((byte)integer);
                    for(int i=0;i<sortPresets.Length;i++)
                    {
                        uint word=unchecked((uint)integer);
                        if(optionName.Equals($"Inv_Sort{i}_primary",StringComparison.OrdinalIgnoreCase))sortPresets[i]=sortPresets[i] with {Primary=word};
                        if(optionName.Equals($"Inv_Sort{i}_secondary",StringComparison.OrdinalIgnoreCase))sortPresets[i]=sortPresets[i] with {Secondary=word};
                        if(optionName.Equals($"Inv_Sort{i}_tertiary",StringComparison.OrdinalIgnoreCase))sortPresets[i]=sortPresets[i] with {Tertiary=word};
                    }
                    if (optionName.Equals("Starmap_Label", StringComparison.OrdinalIgnoreCase)) mapLabel = integer;
                    else if (optionName.Equals("Starmap_ShowCoords", StringComparison.OrdinalIgnoreCase)) mapCoordinates = integer;
                }
                continue;
            }

            if (!sectionName.StartsWith(FloatSectionPrefix, StringComparison.OrdinalIgnoreCase) ||
                !values.TryGetValue(OptionValueKey, out string? rawFloat) ||
                !TryReadPersistedFloat(rawFloat, out float value))
            {
                continue;
            }

            if (optionName.Equals(GraphicsGammaOptionName, StringComparison.OrdinalIgnoreCase))
                graphicsGamma = value;
            else if (optionName.Equals(GraphicsLandPolyOptionName, StringComparison.OrdinalIgnoreCase))
                graphicsLandPoly = value;
            else if (optionName.Equals(GraphicsLandDetailOptionName, StringComparison.OrdinalIgnoreCase))
                graphicsLandDetail = value;
            else if (optionName.Equals(GraphicsSpacePolyOptionName, StringComparison.OrdinalIgnoreCase))
                graphicsSpacePoly = value;
            else if (optionName.Equals(GraphicsTextureSizeOptionName, StringComparison.OrdinalIgnoreCase))
                graphicsTextureSize = value;
            else if (optionName.Equals(RadarGutterOpacityOptionName, StringComparison.OrdinalIgnoreCase))
                radarGutterOpacity = value;
            else if (optionName.Equals("Interface_Tooltip_Delay", StringComparison.OrdinalIgnoreCase))
                tooltipDelay = Math.Clamp(value, 0f, 100f);
            else if (optionName.Equals("Sound_Music", StringComparison.OrdinalIgnoreCase))
                soundMusic = Math.Clamp(value, 0f, 100f);
            else if (optionName.Equals("Sound_SFX", StringComparison.OrdinalIgnoreCase))
                soundSfx = Math.Clamp(value, 0f, 100f);
            else if (optionName.Equals("Sound_Dialog", StringComparison.OrdinalIgnoreCase)) soundDialog = Math.Clamp(value, 0f, 100f);
            else if (optionName.Equals("Starmap_MinFrame", StringComparison.OrdinalIgnoreCase))
                mapMin = value;
            else if (optionName.Equals("Starmap_MaxFrame", StringComparison.OrdinalIgnoreCase))
                mapMax = value;
        }

        return new EnbRetailPlayerOptions(
            bridgeMode,
            graphicsDefault,
            graphicsGamma,
            graphicsLandPoly,
            graphicsLandDetail,
            graphicsSpacePoly,
            graphicsTextureSize,
            radarGutterOpacity,
            cameraGate,
            tooltipDelay,
            throttleRightMouse,
            throttleClickLocks,
            soundMusic,
            soundSfx,
            dopplerEnabled, mapMin, mapMax, mapMaximized, mapLabel, mapCoordinates, cameraWarp,
            cameraShip, cameraLoot, cameraLand, cameraDock, cameraBridge, starbaseDefaultRun, restrictedStatus)
        { AutopilotEnabled=autopilotEnabled, StarbaseThirdPersonMode=starbaseThirdPersonMode, InventoryCargoSort=cargoSort, InventoryVaultSort=vaultSort, InventoryCargoReverse=cargoReverse, InventoryVaultReverse=vaultReverse, InventorySortPresets=sortPresets, InventoryCargoFilter = cargoFilter, InventoryVaultFilter = vaultFilter, SoundDialog = soundDialog, FontOptions = fontOptions, GalaxyMapShowAll = galaxyMapShowAll, TradeSortByName = tradeSortByName, MonitoredChatChannels = [.. NamedChannels.Where(c => monitoredChannels.Contains(c.Channel)).Select(c => c.Channel)] };
    }

    /// <summary>
    /// Current compatibility-path player-options selector. Retail first retains
    /// the fixed/session-ID bytes through SectorJoin's half-swap codec, making
    /// the low x86 dword bswap32(fixedSessionId), then takes the full retained
    /// u64 value modulo 1,000,000,000 before formatting player%d_options.ini.
    /// </summary>
    public static int GetCurrentCompatibilitySelector(uint fixedSessionId)
    {
        uint retainedLowDword = BinaryPrimitives.ReverseEndianness(fixedSessionId);
        return checked((int)((ulong)retainedLowDword % PlayerOptionsSelectorModulus));
    }

    private static bool ReadPersistedBoolean(string rawValue)
        => rawValue.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadPersistedFloat(string rawValue, out float value)
    {
        if (!float.TryParse(
                rawValue.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value) ||
            !float.IsFinite(value))
        {
            value = default;
            return false;
        }

        return true;
    }
}

public sealed record EnbRetailPlayerOptionsSelection(
    EnbRetailPlayerOptions Options,
    int? Selector,
    EnbRetailDataProvenance? Provenance,
    bool UsesPersistedFile);
