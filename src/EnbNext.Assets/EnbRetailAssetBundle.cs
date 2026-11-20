using System.Collections.Immutable;

namespace EnbNext.Assets;

public enum EnbRetailAssetSourceKind
{
    BassetIni,
    CbassetIni,
    EffectIni,
    RenderStatesIni,
    EnvironmentMapsIni,
    SpaceCameraIni,
    StarsIni,
    SoundsIni,
    NebulaIni,
    RadarIni,
    CockpitIni,
    FontSystemIni,
    GutterColorIni,
    MetalEquivalentRgbsIni,
    DecalTexturesIni,
    DecalSwatchesIni,
    SecretDecalTexturesIni,
    FactionIni,
    MfdIni,
    MixFilesDirectory,
}

public sealed record EnbRetailAssetSource(
    EnbRetailAssetSourceKind Kind,
    string Path,
    bool Exists);

/// <summary>
/// Presentation-data provenance that can be carried forward into snapshot
/// diagnostics. Section/key are optional because MIX resources are identified
/// by archive/name instead of INI coordinates.
/// </summary>
public sealed record EnbRetailDataProvenance(
    string Path,
    string? Section = null,
    string? Key = null,
    string? ResourceName = null,
    string? ArchiveName = null);

/// <summary>
/// Offline-loadable aggregate for the retail presentation asset layer. This is
/// intentionally independent from Net7 state and UI/rendering frameworks. It
/// retains the exact selected-install paths and leaves optional/corrupt local
/// presentation inputs absent instead of affecting protocol/session behavior.
/// </summary>
public sealed partial class EnbRetailAssetBundle
{
    /// <summary>Render-side session state; owned here, set by the renderer.</summary>
    public EnbRenderPrepared? RenderPrepared;
    private readonly Dictionary<EnbRetailAssetSourceKind, EnbRetailAssetSource> _sources;
    private readonly object _sessionPresentationSelectionGate = new();
    private readonly Dictionary<uint, EnbRetailCameraModeSelection?> _cameraSelections = [];
    private readonly Dictionary<uint, EnbRetailPlayerOptionsSelection> _playerOptionSelections = [];
    private readonly Dictionary<uint, EnbRetailGraphicsOptions> _graphicsPreviews = [];
    private readonly Dictionary<uint, EnbRetailKeyBindings> _keyBindings = [];
    private readonly Dictionary<uint, EnbRetailShortcuts> _shortcuts = [];
    private EnbRetailSkillCatalog? _shortcutSkills;
    private bool _shortcutSkillsLoaded;
    private bool SaveShortcuts(EnbRetailShortcuts shortcuts,uint character)
    {
        try {shortcuts.SaveOutput(Paths.ClientOutputDirectory,character);return true;}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError("Shortcut assignment could not be saved: {0}",ex.Message);
            return false;
        }
    }

    public EnbRetailShortcuts ResolveShortcuts(uint? fixedSessionId)
    {
        lock (_sessionPresentationSelectionGate)
        {
            uint id = fixedSessionId ?? 0;
            if (_shortcuts.TryGetValue(id, out var saved)) return saved;
            try { saved = EnbRetailShortcuts.Load(Paths, fixedSessionId); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                saved = EnbRetailShortcuts.Resolve(EnbRetailIniDocument.Parse(""), fixedSessionId)
                    with { Diagnostics = ["Saved shortcuts unavailable: " + ex.Message] };
            }
            return _shortcuts[id] = saved;
        }
    }

    public EnbRetailSkillCatalog? ResolveShortcutSkills()
    {
        lock (_sessionPresentationSelectionGate)
        {
            if (!_shortcutSkillsLoaded)
            {
                _shortcutSkills = TryLoad(() => EnbRetailSkillCatalog.Load(Paths.ClientIniDirectory));
                _shortcutSkillsLoaded = true;
            }
            return _shortcutSkills;
        }
    }

    public bool AssignSessionShortcutAbility(uint character, EnbRetailShortcuts expected, EnbRetailShortcutAddress address, EnbRetailSkillAbility ability,bool persist=false)
    {
        lock(_sessionPresentationSelectionGate)
        {
            var catalog=ResolveShortcutSkills();
            if(character==0||!address.IsValid||!ReferenceEquals(ResolveShortcuts(character),expected)||
                catalog?.FindAbility(ability.Name)!=ability||!ability.Activated||catalog.FindFamily(ability.FamilyName) is not {} family)return false;
            var entry=new EnbRetailShortcutEntry(EnbRetailShortcutSource.Skill,address,$"<{family.Id}/><{family.Name}/><{ability.Name}/>",family.Id,family.Name,ability.Name);
            var updated=expected with {Sections=expected.Sections.Select(s=>s with {Entries=
                (s.Source==EnbRetailShortcutSource.Skill?s.Entries.Where(e=>e.Address!=address).Append(entry):s.Entries.Where(e=>e.Address!=address)).ToImmutableArray()}).ToImmutableArray()};
            if(persist&&!SaveShortcuts(updated,character))return false;
            _shortcuts[character]=updated;
            return true;
        }
    }

    public bool MoveSessionShortcut(uint character,EnbRetailShortcuts expected,EnbRetailShortcutEntry source,
        EnbRetailShortcutAddress? destination,bool persist=false)
    {
        lock(_sessionPresentationSelectionGate)
        {
            if(character==0||!ReferenceEquals(ResolveShortcuts(character),expected)||!expected.TryMove(source,destination,out var updated))return false;
            if(ReferenceEquals(updated,expected))return true;
            if(persist&&!SaveShortcuts(updated,character))return false;
            _shortcuts[character]=updated;
            return true;
        }
    }

    public bool AssignSessionShortcutItem(uint character,EnbRetailShortcuts expected,EnbRetailShortcutAddress address,byte inventory,int slot,bool persist=false)
    {
        lock(_sessionPresentationSelectionGate)
        {
            if(character==0 || inventory is not (1 or 2) || slot<0 || !address.IsValid || !ReferenceEquals(ResolveShortcuts(character),expected))return false;
            var source=inventory==2?EnbRetailShortcutSource.Equipment:EnbRetailShortcutSource.Cargo;
            var entry=new EnbRetailShortcutEntry(source,address,$"<{slot.ToString(System.Globalization.CultureInfo.InvariantCulture)}/>",slot,null,null);
            var updated=expected with {Sections=expected.Sections.Select(s=>s with {Entries=
                (s.Source==source?s.Entries.Where(e=>e.Address!=address).Append(entry):s.Entries.Where(e=>e.Address!=address)).ToImmutableArray()}).ToImmutableArray()};
            if(persist&&!SaveShortcuts(updated,character))return false;
            _shortcuts[character]=updated;return true;
        }
    }

    public bool SelectSessionShortcutAbility(uint fixedSessionId, EnbRetailShortcuts expected,
        EnbRetailShortcutEntry entry, EnbRetailSkillAbility ability,bool persist=false)
    {
        lock (_sessionPresentationSelectionGate)
        {
            if (fixedSessionId == 0 || !ReferenceEquals(ResolveShortcuts(fixedSessionId), expected) ||
                entry.Source != EnbRetailShortcutSource.Skill || entry.SavedIndex is null ||
                !ability.FamilyName.Equals(entry.FamilyName, StringComparison.OrdinalIgnoreCase) ||
                expected.Sections.SelectMany(s => s.Entries).Count(e => e == entry) != 1) return false;
            var replacement = entry with { AbilityName = ability.Name,
                Key = $"<{entry.SavedIndex}/><{entry.FamilyName}/><{ability.Name}/>" };
            var updated = expected with { Sections = expected.Sections.Select(s => s with
                { Entries = s.Entries.Select(e => e == entry ? replacement : e).ToImmutableArray() }).ToImmutableArray() };
            if(persist&&!SaveShortcuts(updated,fixedSessionId))return false;
            _shortcuts[fixedSessionId]=updated;
            return true;
        }
    }

    public EnbRetailKeyBindings ResolveKeyBindings(uint? fixedSessionId)
    {
        lock (_sessionPresentationSelectionGate)
        {
            uint id = fixedSessionId ?? 0;
            if (!_keyBindings.TryGetValue(id, out var bindings))
                _keyBindings[id] = bindings = TryLoad(() => EnbRetailKeyBindings.Load(Paths, fixedSessionId))
                    ?? new EnbRetailKeyBindings("unavailable", []);
            return bindings;
        }
    }

    public void SaveControlOptions(uint session, EnbRetailControlOptions options)
    {
        if (session == 0) throw new InvalidOperationException("A character is required to save controls.");
        lock (_sessionPresentationSelectionGate)
        {
            string path = Path.Combine(Paths.ClientOutputDirectory, "keymap.ini");
            string text = options.SaveText(EnbNext.Installation.EnbPathCase.FileExists(path) ? File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(path)) : "", session);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Paths.ClientOutputDirectory);
                File.WriteAllText(temporary, text);
                File.Move(temporary, EnbNext.Installation.EnbPathCase.Resolve(path), true);
                // Next actions are kept in EnbNext's own file, never in the retail keymap.
                EnbNextControls.Save(EnbRetailControlOptions.Section(session), (action, slot) => options.Binding(action, slot)?.Encoded ?? "");
                _keyBindings.Clear();
                _keyBindings[session] = options.Draft with { SourceSection = EnbRetailControlOptions.Section(session) };
            }
            finally { if (EnbNext.Installation.EnbPathCase.FileExists(temporary)) File.Delete(temporary); }
        }
    }

    private EnbRetailAssetBundle(
        EnbInstalledAssetPaths paths,
        Dictionary<EnbRetailAssetSourceKind, EnbRetailAssetSource> sources,
        EnbClientBaseAssetCatalog? clientBaseAssets,
        EnbClientBaseAssetMountCatalog? clientBaseAssetMounts,
        EnbRetailPresentationCatalog? presentationCatalog,
        EnbRetailRenderStateCatalog? renderStates,
        EnbMixResourceCatalog? mixResources,
        EnbMixResourceCatalog? retailMixResources,
        EnbEnvironmentMapCatalog? environmentMaps,
        EnbRetailCameraModeCatalog? spaceCameraModes,
        EnbRetailStarCatalog? stars,
        EnbRetailSoundCatalog? sounds,
        EnbRetailNebulaEnvironmentCatalog? nebulaEnvironment,
        EnbDecalDefinitionCatalog? decals,
        EnbMetalEquivalentRgbCatalog? metalEquivalentRgbs,
        EnbGutterColorCatalog? gutterColors,
        EnbFontSystemCatalog? fonts,
        EnbFactionCatalog? factions,
        EnbInterfaceLayoutCatalog? radarLayout,
        EnbInterfaceLayoutCatalog? cockpitLayout,
        EnbInterfaceLayoutCatalog? mfdLayout, string? textureOverrideDirectory)
    {
        Paths = paths;
        Blood = TryLoad(() => new EnbBloodCatalog(EnbRetailIniDocument.Load(Path.Combine(paths.ClientIniDirectory,"blood.ini"))));
        ProjectedDecals = TryLoad(() => new EnbProjectedDecalCatalog(EnbRetailIniDocument.Load(Path.Combine(paths.ClientIniDirectory, "proj_dec.ini"))));
        _sources = sources;
        ClientBaseAssets = clientBaseAssets;
        ClientBaseAssetMounts = clientBaseAssetMounts;
        PresentationCatalog = presentationCatalog;
        RenderStates = renderStates;
        MixResources = mixResources;
        RetailMixResources = retailMixResources;
        EnvironmentMaps = environmentMaps;
        SpaceCameraModes = spaceCameraModes;
        Stars = stars;
        Sounds = sounds;
        AnimationSounds = TryLoad(() => EnbW3dAnimationSoundCatalog.Parse(
            File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(Path.Combine(paths.ClientIniDirectory, "W3DAnimSound.ini")))));
        NebulaEnvironment = nebulaEnvironment;
        Decals = decals;
        MetalEquivalentRgbs = metalEquivalentRgbs;
        GutterColors = gutterColors;
        Fonts = fonts;
        Factions = factions;
        RadarLayout = radarLayout;
        CockpitLayout = cockpitLayout;
        MfdLayout = mfdLayout;

        FileResources = new EnbRetailFileResolver(Path.GetDirectoryName(paths.ClientExecutablePath)!, retailMixResources);
        W3dFiles = new(FileResources, mixResources);
        RslResources = new EnbRetailRslResourceResolver(mixResources, retailMixResources, FileResources);
        BaseAssetResolver = presentationCatalog is null
            ? null
            : new EnbRetailBaseAssetResolver(
                presentationCatalog,
                mixResources,
                renderStates,
                RslResources,
                FileResources);
        EnbCurrentRetailThumbnailFileSource.TryLoad(Path.GetDirectoryName(paths.ClientExecutablePath)!,
            paths.MixFilesDirectory,out _thumbnailFiles);
        TextureOverrideRoot = textureOverrideDirectory;
        TextureOverrides = textureOverrideDirectory is null ? null : new EnbTextureOverridePack(textureOverrideDirectory);
        Textures = new EnbRetailTextureResolver(mixResources,_thumbnailFiles,TextureOverrides){ClientExecutablePath=paths.ClientExecutablePath};
    }

    public EnbInstalledAssetPaths Paths { get; }
    public EnbBloodCatalog? Blood { get; }
    public EnbProjectedDecalCatalog? ProjectedDecals { get; }

    public IReadOnlyDictionary<EnbRetailAssetSourceKind, EnbRetailAssetSource> Sources => _sources;

    public EnbClientBaseAssetCatalog? ClientBaseAssets { get; }

    public EnbClientBaseAssetMountCatalog? ClientBaseAssetMounts { get; }

    public EnbRetailPresentationCatalog? PresentationCatalog { get; }

    public EnbRetailRenderStateCatalog? RenderStates { get; }

    public EnbMixResourceCatalog? MixResources { get; }

    /// <summary>
    /// Current retail's complete Data/client/mixfiles package namespace (*.MIX).
    /// The older MixResources property remains the established art-only catalog
    /// for render/audit callers whose historical census depends on that scope.
    /// </summary>
    public EnbMixResourceCatalog? RetailMixResources { get; }

    public EnbRetailRslResourceResolver RslResources { get; }
    public EnbRetailFileResolver FileResources { get; }
    public EnbRetailW3dFileResolver W3dFiles { get; }

    public EnbRetailBaseAssetResolver? BaseAssetResolver { get; }

    public EnbRetailTextureResolver Textures { get; private set; }
    public bool TextureOverridesEnabled { get; private set; } = true;
    public long TextureRevision { get; private set; }
    public EnbTextureOverridePack? TextureOverrides { get; private set; }
    private string? TextureOverrideRoot { get; }
    public string? SelectedTexturePack { get; private set; }
    public string?[] GetTexturePackNames()
    {
        var names=new List<string?> { "" };
        if(TextureOverrideRoot is not {} root || !EnbNext.Installation.EnbPathCase.DirectoryExists(root))return names.ToArray();
        try
        {
            if(EnbNext.Installation.EnbPathCase.EnumerateFiles(root).Any(p=>Path.GetExtension(p).Equals(".dds",StringComparison.OrdinalIgnoreCase)))names.Add(null);
            names.AddRange(EnbNext.Installation.EnbPathCase.EnumerateDirectories(root).Where(p=>(File.GetAttributes(EnbNext.Installation.EnbPathCase.Resolve(p))&FileAttributes.ReparsePoint)==0)
                .Select(Path.GetFileName).OrderBy(n=>n,StringComparer.OrdinalIgnoreCase));
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
        return names.ToArray();
    }
    public void SelectTexturePack(string? name)
    {
        if(name is {Length:>0} && (name is "." or ".." || name.IndexOfAny(['/', '\\', ':'])>=0 || name.IndexOfAny(Path.GetInvalidFileNameChars())>=0))
            throw new ArgumentException("Expected a texture pack folder name.",nameof(name));
        string? path=TextureOverrideRoot is {} root && name!="" ? name is null?root:Path.Combine(root,name):null;
        try
        {
            if(path is not null && (!EnbNext.Installation.EnbPathCase.DirectoryExists(path)||(File.GetAttributes(EnbNext.Installation.EnbPathCase.Resolve(path))&FileAttributes.ReparsePoint)!=0 ||
                name is null&&!EnbNext.Installation.EnbPathCase.EnumerateFiles(path).Any(p=>Path.GetExtension(p).Equals(".dds",StringComparison.OrdinalIgnoreCase))))path=null;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { path=null; }
        if(path is null)name="";
        if(SelectedTexturePack==name && TextureOverridesEnabled==(name!=""))return;
        var pack=path is null?null:new EnbTextureOverridePack(path);
        Textures=new EnbRetailTextureResolver(MixResources,_thumbnailFiles,pack){ClientExecutablePath=Paths.ClientExecutablePath};
        TextureOverrides=pack;SelectedTexturePack=name;TextureOverridesEnabled=name!="";TextureRevision++;
    }

    private readonly object _thumbnailGate=new();
    private readonly Dictionary<string,EnbCurrentRetailTextureThumbnail> _textureThumbnails=new(StringComparer.Ordinal);
    private readonly EnbCurrentRetailThumbnailFileSource? _thumbnailFiles;

    public EnbCurrentRetailTextureThumbnail? ResolveTextureThumbnail(string name)
    {
        int terminator=name.IndexOf('\0');
        if(terminator>=0)name=name[..terminator];
        //Contain the native pre-buffer suffix write for malformed names.
        if(name.Length<4 || name.Any(character=>character>255))return null;
        if(TextureOverridesEnabled && TextureOverrides?.ResolveThumbnail(name) is {} replacement)return replacement;
        lock(_thumbnailGate)
        {
            string key=EnbCurrentRetailTextureThumbnailTable.LookupKey(name);
            if(_textureThumbnails.TryGetValue(key,out var cached))return cached;
            // Derive metadata from the selected texture source, never a disk thumbnail cache.
            var generated=_thumbnailFiles is null?null:
                EnbCurrentRetailTextureThumbnailGenerator.Create(name,_thumbnailFiles.Read);
            if(generated is not null)_textureThumbnails.Add(key,generated);
            return generated;
        }
    }

    public EnbEnvironmentMapCatalog? EnvironmentMaps { get; }

    public EnbRetailCameraModeCatalog? SpaceCameraModes { get; }

    public EnbRetailStarCatalog? Stars { get; }

    public EnbRetailSoundCatalog? Sounds { get; }
    public EnbW3dAnimationSoundCatalog? AnimationSounds { get; }

    public EnbRetailNebulaEnvironmentCatalog? NebulaEnvironment { get; }

    public EnbDecalDefinitionCatalog? Decals { get; }

    public EnbMetalEquivalentRgbCatalog? MetalEquivalentRgbs { get; }

    public EnbGutterColorCatalog? GutterColors { get; }

    public EnbFontSystemCatalog? Fonts { get; }

    public EnbFactionCatalog? Factions { get; }

    public EnbInterfaceLayoutCatalog? RadarLayout { get; }

    public EnbInterfaceLayoutCatalog? CockpitLayout { get; }

    public EnbInterfaceLayoutCatalog? MfdLayout { get; }

    public static EnbRetailAssetBundle Load(EnbInstalledAssetPaths paths, string? textureOverrideDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var sources = BuildSources(paths);

        EnbClientBaseAssetCatalog? clientBaseAssets =
            EnbClientBaseAssetCatalog.Load(paths.CbassetIniPath);
        EnbClientBaseAssetMountCatalog? clientBaseAssetMounts =
            EnbClientBaseAssetMountCatalog.Load(paths.CbassetIniPath);
        EnbMixResourceCatalog? mixResources = EnbMixResourceCatalog.Load(paths.MixFilesDirectory);
        EnbMixResourceCatalog? retailMixResources = EnbMixResourceCatalog.LoadRetailPackages(paths.MixFilesDirectory);
        EnbRetailRenderStateCatalog? renderStates = EnbRetailRenderStateCatalog.Load(paths.RsIniPath);

        EnbRetailPresentationCatalog? presentationCatalog = TryLoad(() =>
        {
            var catalog = new EnbRetailPresentationCatalog();
            catalog.LoadBaseAssets(paths.BassetIniPath);
            if (EnbNext.Installation.EnbPathCase.FileExists(paths.EffectIniPath))
                catalog.LoadEffects(paths.EffectIniPath);
            return catalog;
        });

        EnbEnvironmentMapCatalog? environmentMaps =
            TryLoadExisting(paths.EnvironmentMapsIniPath, EnbEnvironmentMapCatalog.Load);
        EnbRetailCameraModeCatalog? spaceCameraModes =
            TryLoadExisting(paths.SpaceCameraIniPath, EnbRetailCameraModeCatalog.Load);
        EnbRetailStarCatalog? stars =
            TryLoadExisting(paths.StarsIniPath, EnbRetailStarCatalog.Load);
        string soundsIniPath = paths.TryResolveSoundsIniPath(out string resolvedSoundsIniPath)
            ? resolvedSoundsIniPath
            : paths.SoundsIniPath;
        EnbRetailSoundCatalog? sounds =
            TryLoadExisting(soundsIniPath, EnbRetailSoundCatalog.Load);
        string nebulaIniPath = paths.TryResolveNebulaIniPath(out string resolvedNebulaIniPath)
            ? resolvedNebulaIniPath
            : paths.NebulaIniPath;
        EnbRetailNebulaEnvironmentCatalog? nebulaEnvironment =
            TryLoadExisting(nebulaIniPath, EnbRetailNebulaEnvironmentCatalog.Load);

        EnbDecalDefinitionCatalog? decals =
            EnbNext.Installation.EnbPathCase.FileExists(paths.DecalTexturesIniPath) &&
            EnbNext.Installation.EnbPathCase.FileExists(paths.DecalSwatchesIniPath) &&
            EnbNext.Installation.EnbPathCase.FileExists(paths.SecretDecalTexturesIniPath)
                ? TryLoad(() => EnbDecalDefinitionCatalog.Load(
                    paths.DecalTexturesIniPath,
                    paths.DecalSwatchesIniPath,
                    paths.SecretDecalTexturesIniPath))
                : null;

        EnbMetalEquivalentRgbCatalog? metalEquivalentRgbs =
            TryLoadExisting(paths.MetalEquivalentRgbsIniPath, EnbMetalEquivalentRgbCatalog.Load);
        EnbGutterColorCatalog? gutterColors =
            TryLoadExisting(paths.GutterColorIniPath, EnbGutterColorCatalog.Load);
        EnbFontSystemCatalog? fonts =
            TryLoadExisting(paths.FontSystemIniPath, EnbFontSystemCatalog.Load);
        EnbFactionCatalog? factions =
            TryLoadExisting(paths.FactionIniPath, EnbFactionCatalog.Load);
        EnbInterfaceLayoutCatalog? radarLayout =
            TryLoadExisting(paths.RadarIniPath, EnbInterfaceLayoutCatalog.Load);
        EnbInterfaceLayoutCatalog? cockpitLayout =
            TryLoadExisting(paths.CockpitIniPath, EnbInterfaceLayoutCatalog.Load);
        EnbInterfaceLayoutCatalog? mfdLayout =
            TryLoadExisting(paths.MfdIniPath, EnbInterfaceLayoutCatalog.Load);

        return new EnbRetailAssetBundle(
            paths,
            sources,
            clientBaseAssets,
            clientBaseAssetMounts,
            presentationCatalog,
            renderStates,
            mixResources,
            retailMixResources,
            environmentMaps,
            spaceCameraModes,
            stars,
            sounds,
            nebulaEnvironment,
            decals,
            metalEquivalentRgbs,
            gutterColors,
            fonts,
            factions,
            radarLayout,
            cockpitLayout,
            mfdLayout, textureOverrideDirectory);
    }

    public bool TryGetSource(
        EnbRetailAssetSourceKind kind,
        out EnbRetailAssetSource source)
        => _sources.TryGetValue(kind, out source!);

    /// <summary>
    /// Resolves the current retail camera-mode source for a session. Retail
    /// attempts Data/client/output/cam&lt;signed-bswap32(fixedId)&gt;.ini first and
    /// falls back to Data/client/ini/spacecam.ini. Parsed selections are cached
    /// per fixed/session ID so world snapshot publication performs no per-frame
    /// file I/O.
    /// </summary>
    public void SaveCameraMode(uint fixedSessionId, EnbRetailCameraMode mode)
    {
        if (fixedSessionId == 0) throw new ArgumentException("Camera save needs a character identity");
        lock (_sessionPresentationSelectionGate)
        {
            var selected = ResolveCameraModes(fixedSessionId) ?? throw new InvalidOperationException("Camera modes unavailable");
            string path = Path.Combine(Paths.ClientOutputDirectory,
                $"cam{EnbRetailCameraModeCatalog.GetSessionOverrideSelector(fixedSessionId)}.ini");
            string text = File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(EnbNext.Installation.EnbPathCase.FileExists(path) ? path : selected.Provenance.Path));
            string updated = EnbRetailCameraModeWriter.Update(text, mode);
            Directory.CreateDirectory(Paths.ClientOutputDirectory);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, updated);
                File.Move(temporary, EnbNext.Installation.EnbPathCase.Resolve(path), overwrite: true);
                _cameraSelections.Remove(fixedSessionId);
            }
            finally { if (EnbNext.Installation.EnbPathCase.FileExists(temporary)) File.Delete(temporary); }
        }
    }

    public EnbRetailCameraModeSelection? ResolveCameraModes(uint? fixedSessionId)
    {
        if (fixedSessionId is not uint sessionId || sessionId == 0)
            return SpaceCameraModes is null
                ? null
                : new EnbRetailCameraModeSelection(
                    SpaceCameraModes,
                    new EnbRetailDataProvenance(Paths.SpaceCameraIniPath),
                    UsesSessionOverride: false,
                    SessionOverrideSelector: null);

        lock (_sessionPresentationSelectionGate)
        {
            if (_cameraSelections.TryGetValue(sessionId, out EnbRetailCameraModeSelection? cached))
                return cached;

            int selector = EnbRetailCameraModeCatalog.GetSessionOverrideSelector(sessionId);
            string overridePath = Path.Combine(Paths.ClientOutputDirectory, $"cam{selector}.ini");
            EnbRetailCameraModeCatalog? overrideCatalog =
                TryLoadExisting(overridePath, EnbRetailCameraModeCatalog.Load);

            EnbRetailCameraModeSelection? selected = overrideCatalog is not null
                ? new EnbRetailCameraModeSelection(
                    overrideCatalog,
                    new EnbRetailDataProvenance(overridePath),
                    UsesSessionOverride: true,
                    SessionOverrideSelector: selector)
                : SpaceCameraModes is null
                    ? null
                    : new EnbRetailCameraModeSelection(
                        SpaceCameraModes,
                        new EnbRetailDataProvenance(Paths.SpaceCameraIniPath),
                        UsesSessionOverride: false,
                        SessionOverrideSelector: selector);

            _cameraSelections[sessionId] = selected;
            return selected;
        }
    }

    /// <summary>
    /// Resolves the current retail per-player options source for a session.
    /// Current retail initializes Bridge_Mode=false, then loads
    /// Data/client/output/player&lt;selector&gt;_options.ini when present. Results are
    /// cached per fixed/session ID so presentation snapshot publication performs
    /// no per-frame file I/O.
    /// </summary>
    public void SaveCinematicOptions(uint fixedSessionId, bool warp, bool gate)
        => SaveBooleanOptions(fixedSessionId, [("Camera_Warp", warp), ("Camera_Gate", gate)]);
    public void SaveCinematicOptions(uint fixedSessionId, EnbRetailCameraOptions options)
        => SaveBooleanOptions(fixedSessionId, options.Values);

    public void SaveGraphicsOptions(uint fixedSessionId, EnbRetailGraphicsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (fixedSessionId == 0) throw new ArgumentException("Player settings need a character identity");
        options = options.Clamp();
        lock (_sessionPresentationSelectionGate)
        {
            string path = Path.Combine(Paths.ClientOutputDirectory,
                $"player{EnbRetailPlayerOptionsCatalog.GetCurrentCompatibilitySelector(fixedSessionId)}_options.ini");
            string existing = EnbNext.Installation.EnbPathCase.FileExists(path) ? File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(path)) : "";
            string updated = EnbRetailCinematicOptionsWriter.UpdateBooleans(existing,
                [("Graphics_Default", options.UseRecommendedValues)]);
            updated = EnbRetailCinematicOptionsWriter.UpdateFloats(updated,
            [
                ("Graphics_Gamma", options.Gamma),
                ("Graphics_Land_Poly", options.LandPoly),
                ("Graphics_Land_Detail", options.LandDetail),
                ("Graphics_Space_Poly", options.SpacePoly),
                ("Graphics_Texture_Size", options.TextureSize),
            ]);
            Directory.CreateDirectory(Paths.ClientOutputDirectory);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, updated);
                File.Move(temporary, EnbNext.Installation.EnbPathCase.Resolve(path), overwrite: true);
                _playerOptionSelections.Remove(fixedSessionId);
            }
            finally { if (EnbNext.Installation.EnbPathCase.FileExists(temporary)) File.Delete(temporary); }
        }
    }

    /// <summary>Session-local graphics preview; never writes the player options file.</summary>
    public void SetGraphicsPreview(uint fixedSessionId, EnbRetailGraphicsOptions? options)
    {
        if (fixedSessionId == 0) throw new ArgumentException("Player settings need a character identity");
        lock (_sessionPresentationSelectionGate)
        {
            if (options is null) _graphicsPreviews.Remove(fixedSessionId);
            else _graphicsPreviews[fixedSessionId] = options.Clamp();
        }
    }

    private EnbRetailPlayerOptionsSelection WithGraphicsPreview(uint fixedSessionId,
        EnbRetailPlayerOptionsSelection selected)
    {
        if (!_graphicsPreviews.TryGetValue(fixedSessionId, out var preview)) return selected;
        return selected with { Options = selected.Options with
        {
            GraphicsDefault = preview.UseRecommendedValues,
            GraphicsGamma = preview.Gamma,
            GraphicsLandPoly = preview.LandPoly,
            GraphicsLandDetail = preview.LandDetail,
            GraphicsSpacePoly = preview.SpacePoly,
            GraphicsTextureSize = preview.TextureSize,
        } };
    }

    public void SaveBridgeMode(uint fixedSessionId, bool firstPerson)
        => SaveBooleanOptions(fixedSessionId, [("Bridge_Mode", firstPerson)]);

    public void SaveStarbaseThirdPersonMode(uint fixedSessionId,bool thirdPerson)
        => SaveBooleanOptions(fixedSessionId,[("Starbase_Third_Person_Mode",thirdPerson)]);

    public void ToggleStarbaseDefaultRun(uint fixedSessionId)
    {
        lock (_sessionPresentationSelectionGate)
            SaveBooleanOptions(fixedSessionId, [("Starbase_Default_Run", !ResolvePlayerOptions(fixedSessionId).Options.StarbaseDefaultRun)]);
    }

    public void SavePresenceVisibility(uint fixedSessionId, bool restricted)
        => SaveBooleanOptions(fixedSessionId, [("RestrictedStatus", restricted)]);

    public void SaveGalaxyMapShowAll(uint fixedSessionId, bool showAll)
        => SaveBooleanOptions(fixedSessionId, [("Galaxymap_Show_All", showAll)]);

    public void SaveTradeSortByName(uint fixedSessionId, bool byName)
        => SaveBooleanOptions(fixedSessionId, [("Trade_Sort_By_Name", byName)]);

    private void SaveBooleanOptions(uint fixedSessionId, (string Name, bool Enabled)[] values)
    {
        if (fixedSessionId == 0) throw new ArgumentException("Player settings need a character identity");
        lock (_sessionPresentationSelectionGate)
        {
            string path = Path.Combine(Paths.ClientOutputDirectory,
                $"player{EnbRetailPlayerOptionsCatalog.GetCurrentCompatibilitySelector(fixedSessionId)}_options.ini");
            string updated = EnbRetailCinematicOptionsWriter.UpdateBooleans(EnbNext.Installation.EnbPathCase.FileExists(path) ? File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(path)) : "", values);
            Directory.CreateDirectory(Paths.ClientOutputDirectory);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, updated);
                File.Move(temporary, EnbNext.Installation.EnbPathCase.Resolve(path), overwrite: true);
                _playerOptionSelections.Remove(fixedSessionId);
            }
            finally { if (EnbNext.Installation.EnbPathCase.FileExists(temporary)) File.Delete(temporary); }
        }
    }

    public EnbRetailPlayerOptionsSelection ResolvePlayerOptions(uint? fixedSessionId)
    {
        if (fixedSessionId is not uint sessionId || sessionId == 0)
        {
            return new EnbRetailPlayerOptionsSelection(
                new EnbRetailPlayerOptions(BridgeMode: false),
                Selector: null,
                Provenance: null,
                UsesPersistedFile: false);
        }

        lock (_sessionPresentationSelectionGate)
        {
            if (_playerOptionSelections.TryGetValue(sessionId, out EnbRetailPlayerOptionsSelection? cached))
                return WithSettingsPreview(sessionId, WithGraphicsPreview(sessionId, cached));

            int selector = EnbRetailPlayerOptionsCatalog.GetCurrentCompatibilitySelector(sessionId);
            string path = Path.Combine(Paths.ClientOutputDirectory, $"player{selector}_options.ini");
            EnbRetailPlayerOptions? options =
                TryLoadExisting(path, EnbRetailPlayerOptionsCatalog.Load);

            EnbRetailPlayerOptionsSelection selected = options is null
                ? new EnbRetailPlayerOptionsSelection(
                    new EnbRetailPlayerOptions(BridgeMode: false),
                    selector,
                    Provenance: null,
                    UsesPersistedFile: false)
                : new EnbRetailPlayerOptionsSelection(
                    options,
                    selector,
                    new EnbRetailDataProvenance(path),
                    UsesPersistedFile: true);

            _playerOptionSelections[sessionId] = selected;
            return WithSettingsPreview(sessionId, WithGraphicsPreview(sessionId, selected));
        }
    }

    public EnbRetailDataProvenance BaseAssetProvenance(int selector)
        => new(Paths.BassetIniPath, $"BASE-{selector}");

    public EnbRetailDataProvenance EffectProvenance(int definitionId)
        => new(Paths.EffectIniPath, $"EFFECT-{definitionId}");

    public EnbRetailDataProvenance ClientBaseAssetProvenance(int selector)
        => new(Paths.CbassetIniPath, $"BASE-{selector}");

    private static Dictionary<EnbRetailAssetSourceKind, EnbRetailAssetSource> BuildSources(
        EnbInstalledAssetPaths paths)
    {
        var result = new Dictionary<EnbRetailAssetSourceKind, EnbRetailAssetSource>();

        AddFile(EnbRetailAssetSourceKind.BassetIni, paths.BassetIniPath);
        AddFile(EnbRetailAssetSourceKind.CbassetIni, paths.CbassetIniPath);
        AddFile(EnbRetailAssetSourceKind.EffectIni, paths.EffectIniPath);
        AddFile(EnbRetailAssetSourceKind.RenderStatesIni, paths.RsIniPath);
        AddFile(EnbRetailAssetSourceKind.EnvironmentMapsIni, paths.EnvironmentMapsIniPath);
        AddFile(EnbRetailAssetSourceKind.SpaceCameraIni, paths.SpaceCameraIniPath);
        AddFile(EnbRetailAssetSourceKind.StarsIni, paths.StarsIniPath);
        AddFile(
            EnbRetailAssetSourceKind.SoundsIni,
            paths.TryResolveSoundsIniPath(out string soundsIniPath) ? soundsIniPath : paths.SoundsIniPath);
        AddFile(
            EnbRetailAssetSourceKind.NebulaIni,
            paths.TryResolveNebulaIniPath(out string nebulaIniPath) ? nebulaIniPath : paths.NebulaIniPath);
        AddFile(EnbRetailAssetSourceKind.RadarIni, paths.RadarIniPath);
        AddFile(EnbRetailAssetSourceKind.CockpitIni, paths.CockpitIniPath);
        AddFile(EnbRetailAssetSourceKind.FontSystemIni, paths.FontSystemIniPath);
        AddFile(EnbRetailAssetSourceKind.GutterColorIni, paths.GutterColorIniPath);
        AddFile(EnbRetailAssetSourceKind.MetalEquivalentRgbsIni, paths.MetalEquivalentRgbsIniPath);
        AddFile(EnbRetailAssetSourceKind.DecalTexturesIni, paths.DecalTexturesIniPath);
        AddFile(EnbRetailAssetSourceKind.DecalSwatchesIni, paths.DecalSwatchesIniPath);
        AddFile(EnbRetailAssetSourceKind.SecretDecalTexturesIni, paths.SecretDecalTexturesIniPath);
        AddFile(EnbRetailAssetSourceKind.FactionIni, paths.FactionIniPath);
        AddFile(EnbRetailAssetSourceKind.MfdIni, paths.MfdIniPath);
        result[EnbRetailAssetSourceKind.MixFilesDirectory] = new EnbRetailAssetSource(
            EnbRetailAssetSourceKind.MixFilesDirectory,
            paths.MixFilesDirectory,
            EnbNext.Installation.EnbPathCase.DirectoryExists(paths.MixFilesDirectory));

        return result;

        void AddFile(EnbRetailAssetSourceKind kind, string path)
            => result[kind] = new EnbRetailAssetSource(kind, path, EnbNext.Installation.EnbPathCase.FileExists(path));
    }

    private static T? TryLoadExisting<T>(string path, Func<string, T> loader)
        where T : class
    {
        if (!EnbNext.Installation.EnbPathCase.FileExists(path))
            return null;
        return TryLoad(() => loader(path));
    }

    private static T? TryLoad<T>(Func<T> loader)
        where T : class
    {
        try
        {
            return loader();
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            FormatException or
            OverflowException or
            System.Security.SecurityException)
        {
            return null;
        }
    }
}
