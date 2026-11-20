namespace EnbNext.Assets;

/// <summary>
/// Resolution state for an authored RSL FILE dependency. Current retail uses
/// FILE as a resource-load hint/source while OBJECT remains the render-object
/// prototype name, so this deliberately does not collapse the two strings.
/// </summary>
public enum EnbRslFileDependencyStatus
{
    NotAuthored,
    ResolvedW3d,
    MissingResource,
    InvalidW3d,
    ResolvedEffectPass,
    InvalidEffectPass,
}

/// <summary>
/// One authored FILE dependency from rs.ini after lookup in the selected
/// installation's MIX archives. Texture references are resolved with the same
/// .tga -> .dds compatibility rule as ordinary FILE/NAME BaseAssets.
/// </summary>
public sealed record EnbResolvedRslFileDependency(
    string LogicalFileName,
    EnbRslFileDependencyStatus Status,
    EnbMixResourceLocation? ResourceLocation = null,
    EnbW3dModel? Model = null,
    IReadOnlyList<EnbResolvedTextureReference>? Textures = null,
    string? Diagnostic = null,
    EnbCurrentRetailRslEffectPassDefinition? EffectPass = null,
    string? LooseFilePath = null)
{
    public EnbCurrentRetailW3dPrototypeFile? PrototypeFile {get;init;}
    public bool IsResolved => Status is EnbRslFileDependencyStatus.ResolvedW3d or EnbRslFileDependencyStatus.ResolvedEffectPass;

    public int MissingTextureCount => Textures?.Count(texture => texture.IsMissing) ?? 0;
}

/// <summary>
/// Asset-side resource view of one RSL state. Runtime state-machine execution
/// is intentionally not modeled here. OBJECT, ANIM/SUFFIX, and SOUND remain
/// authored identifiers while FILE is represented as its own resource
/// dependency.
/// </summary>
public sealed record EnbResolvedRslStateResource(
    EnbRslStateDefinition Definition,
    EnbResolvedRslFileDependency FileDependency,
    IReadOnlyList<int> ReferencedStateIds,
    IReadOnlyList<int> MissingReferencedStateIds)
{
    public string? ObjectPrototypeName => Definition.Object;

    public string? AnimationName => Definition.Animation;

    public string? AnimationSuffix => Definition.Suffix;

    public string? SoundName => Definition.Sound;
}

/// <summary>
/// Fully linked local-data view of one installed RSL library. The graph closes
/// authored FILE dependencies and the state-reference edges actually consumed by
/// the current per-type factories. Sound preserves authored NEXT in its definition
/// but only LINK is a current Sound runtime/resource edge.
/// </summary>
public sealed class EnbResolvedRslLibraryResourceGraph
{
    private readonly Dictionary<int, EnbResolvedRslStateResource> _statesById;

    internal EnbResolvedRslLibraryResourceGraph(
        EnbRslLibraryDefinition library,
        IReadOnlyList<EnbResolvedRslStateResource> states,
        IReadOnlyList<int> missingReferencedStateIds)
    {
        Library = library;
        States = states;
        MissingReferencedStateIds = missingReferencedStateIds;
        _statesById = states.ToDictionary(state => state.Definition.StateId);

        MissingFileCount = states.Count(state =>
            state.FileDependency.Status == EnbRslFileDependencyStatus.MissingResource);
        InvalidW3dCount = states.Count(state =>
            state.FileDependency.Status == EnbRslFileDependencyStatus.InvalidW3d);
        InvalidEffectPassCount = states.Count(state =>
            state.FileDependency.Status == EnbRslFileDependencyStatus.InvalidEffectPass);
        MissingTextureCount = states.Sum(state => state.FileDependency.MissingTextureCount);
    }

    public EnbRslLibraryDefinition Library { get; }

    public IReadOnlyList<EnbResolvedRslStateResource> States { get; }

    public IReadOnlyList<int> MissingReferencedStateIds { get; }

    public int MissingFileCount { get; }

    public int InvalidW3dCount { get; }
    public int InvalidEffectPassCount { get; }

    public int MissingTextureCount { get; }

    public bool IsComplete =>
        MissingFileCount == 0 &&
        InvalidW3dCount == 0 &&
        InvalidEffectPassCount == 0 &&
        MissingTextureCount == 0 &&
        MissingReferencedStateIds.Count == 0;

    /// <summary>Creates instance-local LINK pointer metadata without changing the authored library.</summary>
    public EnbResolvedRslLibraryResourceGraph WithResolvedLinkTarget(int stateId,int? targetStateId)
    {
        if(!TryFindState(stateId,out var source) || source.Definition.Type!=EnbRslStateType.Link ||
            targetStateId is int target && !TryFindState(target,out _))
            throw new ArgumentException("A resolved Link instance and an existing or null target are required");
        var states=States.Select(state=>state.Definition.StateId==stateId
            ? state with {Definition=state.Definition with {LinkStateId=targetStateId},
                ReferencedStateIds=targetStateId is int resolved ? new[]{resolved} : Array.Empty<int>(),
                MissingReferencedStateIds=Array.Empty<int>()} : state).ToArray();
        var missing=states.SelectMany(state=>state.MissingReferencedStateIds).Distinct().Order().ToArray();
        return new(Library,states,missing);
    }

    public bool TryFindState(int stateId, out EnbResolvedRslStateResource state)
        => _statesById.TryGetValue(stateId, out state!);
}

/// <summary>
/// Resolves rs.ini library resources against the selected retail installation.
///
/// Proven current-retail distinction retained by this type:
/// - FILE is a resource-load/source string;
/// - OBJECT is the render-object prototype name requested from the resource
///   manager;
/// - ANIM and SUFFIX identify animation behavior;
/// - SOUND remains an authored audio identifier;
/// - Animation/Object NEXT/LINK references remain per-factory state edges;
/// - current Sound consumes LINK but not authored NEXT.
///
/// This class only builds immutable asset descriptors. It does not run the RSL
/// state machine or depend on Net7 packet/world types.
/// </summary>
public sealed class EnbRetailRslResourceResolver
{
    // Retail 764030 retains the library; 45EF80 prepares each template once.
    // Key by catalog definition identity so replacing a catalog creates new templates.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<EnbRslLibraryDefinition, EnbResolvedRslLibraryResourceGraph> _libraries = new();
    private readonly EnbMixResourceCatalog? _resources;
    private readonly EnbMixResourceCatalog? _effectResources;
    private readonly EnbRetailFileResolver? _effectFileProvider;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, EnbResolvedRslFileDependency> _effectFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, EnbResolvedRslFileDependency> _files =
        new(StringComparer.OrdinalIgnoreCase);

    public EnbRetailRslResourceResolver(EnbMixResourceCatalog? resources, EnbMixResourceCatalog? effectResources = null,
        EnbRetailFileResolver? effectFileProvider = null)
    {
        _resources = resources;
        _effectResources = effectResources ?? resources;
        _effectFileProvider = effectFileProvider;
    }

    public EnbResolvedRslLibraryResourceGraph Resolve(EnbRslLibraryDefinition library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return _libraries.GetValue(library, ResolveCore);
    }

    private EnbResolvedRslLibraryResourceGraph ResolveCore(EnbRslLibraryDefinition library)
    {
        var missingReferences = new SortedSet<int>();
        var states = new List<EnbResolvedRslStateResource>(library.States.Count);

        foreach (EnbRslStateDefinition definition in library.States)
        {
            IReadOnlyList<int> references = BuildStateReferences(definition);
            int[] missing = references
                .Where(stateId => !library.TryFindState(stateId, out _))
                .Distinct()
                .Order()
                .ToArray();

            foreach (int stateId in missing)
                missingReferences.Add(stateId);

            states.Add(new EnbResolvedRslStateResource(
                definition,
                definition.Type == EnbRslStateType.EffectPass ? ResolveEffectPassDependency(definition.File) : ResolveFileDependency(definition.File),
                references,
                missing));
        }

        return new EnbResolvedRslLibraryResourceGraph(
            library,
            states,
            missingReferences.ToArray());
    }

    private EnbResolvedRslFileDependency ResolveFileDependency(string? authoredFile)
    {
        string? logicalName = authoredFile?.Trim();
        if (string.IsNullOrEmpty(logicalName))
        {
            return new EnbResolvedRslFileDependency(
                string.Empty,
                EnbRslFileDependencyStatus.NotAuthored);
        }

        return _files.GetOrAdd(logicalName, ResolveFileDependencyCore);
    }

    private EnbResolvedRslFileDependency ResolveEffectPassDependency(string? authoredFile)
    {
        if (string.IsNullOrEmpty(authoredFile))
            return new(string.Empty, EnbRslFileDependencyStatus.NotAuthored);
        return _effectFiles.GetOrAdd(authoredFile, name =>
        {
            byte[] bytes = [];
            EnbMixResourceLocation? location = null;
            string? loosePath = null;
            bool found = _effectFileProvider is not null
                ? _effectFileProvider.TryRead(name, out bytes, out location, out loosePath)
                : _effectResources is not null && _effectResources.TryReadResource(name, out bytes, out location);
            if (!found)
                return new(name, EnbRslFileDependencyStatus.MissingResource,
                    Diagnostic: $"RSL effect-pass INI '{name}' is not available in the installed resources.");
            var ini = EnbRetailIniDocument.Parse(System.Text.Encoding.Latin1.GetString(bytes));
            if (!EnbCurrentRetailRslEffectPassDefinition.TryRead(ini, out var pass, out var diagnostic))
                return new(name, EnbRslFileDependencyStatus.InvalidEffectPass, location, Diagnostic: diagnostic, LooseFilePath: loosePath);
            EnbMixResourceLocation? textureLocation = null;
            if (_resources is not null && pass.Texture.Length != 0 &&
                _resources.TryLocateResource(pass.Texture, out var resolved)) textureLocation = resolved;
            EnbResolvedTextureReference[] textures = pass.Texture.Length == 0 ? [] : [new(pass.Texture, textureLocation)];
            return new(name, EnbRslFileDependencyStatus.ResolvedEffectPass, location,
                Textures: textures, EffectPass: pass, LooseFilePath: loosePath);
        });
    }

    private EnbResolvedRslFileDependency ResolveFileDependencyCore(string logicalName)
    {
        if (_effectFileProvider is not null)
        {
            var files = new EnbRetailW3dFileResolver(_effectFileProvider, _resources);
            if (files.TryResolve(logicalName, string.Empty, out var source, out var diagnostic, out bool loaded))
                return new(logicalName, EnbRslFileDependencyStatus.ResolvedW3d,
                    source.ResourceLocation, source.Model, source.Textures, LooseFilePath: source.LooseFilePath){PrototypeFile=source.PrototypeFile};
            return new(logicalName, loaded ? EnbRslFileDependencyStatus.InvalidW3d : EnbRslFileDependencyStatus.MissingResource,
                Diagnostic: diagnostic);
        }
        if (_resources is null ||
            !_resources.TryReadResource(
                logicalName,
                out byte[] bytes,
                out EnbMixResourceLocation location))
        {
            return new EnbResolvedRslFileDependency(
                logicalName,
                EnbRslFileDependencyStatus.MissingResource,
                Diagnostic: $"RSL FILE resource '{logicalName}' is not available in the installed MIX archives.");
        }

        if (!EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes, out var publication,out _))
        {
            return new EnbResolvedRslFileDependency(
                logicalName,
                EnbRslFileDependencyStatus.InvalidW3d,
                location,
                Diagnostic: $"RSL FILE resource '{location.Name}' is not a supported/valid W3D model.");
        }

        var model=publication.Model;
        return new EnbResolvedRslFileDependency(
            logicalName,
            EnbRslFileDependencyStatus.ResolvedW3d,
            location,
            model,
            EnbW3dResourceDependencies.ResolveTextureDependencies(model, _resources)){PrototypeFile=publication};
    }

    private static IReadOnlyList<int> BuildStateReferences(EnbRslStateDefinition state)
    {
        var references = new List<int>(2);
        // Current Sound factory consumes LINK and MESSAGE but never reads NEXT.
        // Preserve authored NEXT in the catalog for archaeology, but do not turn
        // it into a runtime/resource edge for Sound states.
        // Link factory0045C58D..C5A7 supplies only LINK to0045A090;
        // authored NEXT is not consumed by this class either.
        if (state.Type is not (EnbRslStateType.Sound or EnbRslStateType.Link or EnbRslStateType.Message) && state.NextStateId is int next)
            references.Add(next);
        if (state.LinkStateId is int link && !references.Contains(link))
            references.Add(link);
        return references;
    }
}

/// <summary>
/// Shared W3D dependency helpers used by ordinary BaseAssets and RSL FILE
/// dependencies. Kept asset-internal so the observable contracts remain the
/// typed resolution records above rather than a second resolver API.
/// </summary>
internal static class EnbW3dResourceDependencies
{
    public static IReadOnlyList<EnbResolvedTextureReference> ResolveTextureDependencies(
        EnbW3dModel model,
        EnbMixResourceCatalog? resources)
    {
        ArgumentNullException.ThrowIfNull(model);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var textures = new List<EnbResolvedTextureReference>();

        foreach (EnbW3dMesh mesh in model.Meshes)
        {
            foreach (string authoredName in mesh.TextureNames)
            {
                string logicalName = authoredName.Trim();
                if (logicalName.Length == 0 || !seen.Add(logicalName))
                    continue;

                EnbMixResourceLocation? location = null;
                if (resources is not null &&
                    resources.TryLocateResource(logicalName, out EnbMixResourceLocation resolved))
                {
                    location = resolved;
                }

                textures.Add(new EnbResolvedTextureReference(logicalName, location));
            }

            foreach (EnbW3dLegacyRootTexture legacyTexture in mesh.LegacyRootTextures)
            {
                string logicalName = legacyTexture.Name.Trim();
                if (logicalName.Length == 0 || !seen.Add(logicalName))
                    continue;

                EnbMixResourceLocation? location = null;
                if (resources is not null &&
                    resources.TryLocateResource(logicalName, out EnbMixResourceLocation resolved))
                {
                    location = resolved;
                }

                textures.Add(new EnbResolvedTextureReference(logicalName, location));
            }
        }

        return textures;
    }
}
