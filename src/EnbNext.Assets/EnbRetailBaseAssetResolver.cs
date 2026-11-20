namespace EnbNext.Assets;

public enum EnbBaseAssetResolutionStatus
{
    MissingDefinition,
    NullObject,
    W3d,
    RslReference,
    ProceduralWidth,
    MissingResource,
    MissingRslLibrary,
    InvalidW3d,
    UnsupportedDefinition,
    NoRenderObject,
}

/// <summary>
/// Resource-library descriptor carried by a retail basset.ini RSL branch.
/// The IDs remain authored selectors while Library and InitialStateCommands
/// bind them to the installed rs.ini state library when that catalog is loaded.
/// </summary>
public sealed record EnbResolvedRslReference(
    int RslId,
    int? LinkId,
    int? RsId,
    EnbRslLibraryDefinition? Library,
    IReadOnlyList<EnbRslRenderStateCommand> InitialStateCommands,
    EnbResolvedRslLibraryResourceGraph? ResourceGraph = null);

public sealed record EnbResolvedTextureReference(
    string LogicalName,
    EnbMixResourceLocation? Location)
{
    public bool IsMissing => Location is null;
}

/// <summary>
/// A FILE/NAME BaseAsset after its W3D file has been located in the installed
/// MIX archives and decoded into the generic asset model. RequestedObjectName
/// is the basset.ini NAME value and remains separate from file-level decode.
/// </summary>
public sealed record EnbResolvedW3dResource(
    string LogicalFileName,
    string RequestedObjectName,
    EnbMixResourceLocation ResourceLocation,
    EnbW3dModel Model,
    IReadOnlyList<EnbResolvedTextureReference> Textures,
    EnbCurrentRetailW3dAggregateResolution? CurrentRetailAggregate = null)
{
    public int MissingTextureCount => Textures.Count(texture => texture.IsMissing);
    public EnbW3dPrototypeEntry? RegisteredPrototype { get; init; }
    public long PrototypeRegistration { get; init; }
    public string? LooseFilePath { get; init; }
    public EnbCurrentRetailW3dPrototypeFile? PrototypeFile {get;init;}
    public EnbCurrentRetailW3dPrototypeFileEntry? AggregatePrototype {get;init;}
    public IReadOnlyList<EnbResolvedW3dResource> AcquisitionPublications {get;init;}=[];
}

public sealed record EnbBaseAssetResolution(
    int Selector,
    EnbBaseAssetResolutionStatus Status,
    EnbBaseAssetDefinition Definition,
    EnbResolvedW3dResource? W3d = null,
    EnbResolvedRslReference? Rsl = null,
    float? ProceduralWidth = null,
    string? Diagnostic = null)
{
    public bool IsResolved => Status is
        EnbBaseAssetResolutionStatus.NoRenderObject or EnbBaseAssetResolutionStatus.NullObject or
        EnbBaseAssetResolutionStatus.W3d or
        EnbBaseAssetResolutionStatus.RslReference or
        EnbBaseAssetResolutionStatus.ProceduralWidth;
}

/// <summary>
/// Asset-only bridge from retail BaseAsset selectors to installed presentation
/// resources. It owns no Net7/world semantics and performs no renderer work.
///
/// FILE/NAME branches are materialized through the user's MIX archives into an
/// <see cref="EnbW3dModel"/>. RSL branches bind to the installed rs.ini catalog
/// and expose the exact initial state-command sequence without owning runtime
/// state or Net7 packet behavior.
/// </summary>
public sealed class EnbRetailBaseAssetResolver
{
    private readonly EnbRetailPresentationCatalog _catalog;
    private readonly EnbMixResourceCatalog? _resources;
    private readonly EnbRetailRenderStateCatalog? _renderStates;
    private readonly EnbRetailRslResourceResolver _rslResources;
    private readonly EnbRetailFileResolver? _files;

    public EnbRetailBaseAssetResolver(
        EnbRetailPresentationCatalog catalog,
        EnbMixResourceCatalog? resources,
        EnbRetailRenderStateCatalog? renderStates = null,
        EnbRetailRslResourceResolver? rslResources = null,
        EnbRetailFileResolver? files = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _resources = resources;
        _files = files;
        _renderStates = renderStates;
        _rslResources = rslResources ?? new EnbRetailRslResourceResolver(resources);
    }

    public EnbBaseAssetResolution Resolve(int selector,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes = null)
    {
        EnbBaseAssetDefinition definition = _catalog.ResolveBaseAsset(selector);

        return definition.Kind switch
        {
            EnbBaseAssetDefinitionKind.Missing => new EnbBaseAssetResolution(
                selector,
                EnbBaseAssetResolutionStatus.MissingDefinition,
                definition,
                Diagnostic: $"BASE-{selector} is not available in basset.ini."),

            //006F561D ->006F568D: a present section without a constructor key returns NULL.
            EnbBaseAssetDefinitionKind.NoRenderObject => new(selector,EnbBaseAssetResolutionStatus.NoRenderObject,definition),
            EnbBaseAssetDefinitionKind.NullObject => new EnbBaseAssetResolution(
                selector,
                EnbBaseAssetResolutionStatus.NullObject,
                definition),

            EnbBaseAssetDefinitionKind.Rsl => ResolveRsl(selector, definition),

            EnbBaseAssetDefinitionKind.FileName => ResolveW3d(selector, definition, registeredPrototypes),

            EnbBaseAssetDefinitionKind.ProceduralWidth => new EnbBaseAssetResolution(
                selector,
                EnbBaseAssetResolutionStatus.ProceduralWidth,
                definition,
                ProceduralWidth: definition.Width),

            _ => new EnbBaseAssetResolution(
                selector,
                EnbBaseAssetResolutionStatus.UnsupportedDefinition,
                definition,
                Diagnostic: $"BASE-{selector} uses a basset.ini form not yet represented by the asset layer."),
        };
    }

    private EnbBaseAssetResolution ResolveRsl(
        int selector,
        EnbBaseAssetDefinition definition)
    {
        if (definition.RslId is not int rslId)
        {
            return new EnbBaseAssetResolution(
                selector,
                EnbBaseAssetResolutionStatus.MissingDefinition,
                definition,
                Diagnostic: $"BASE-{selector} selected the RSL branch without a usable RSLID.");
        }

        EnbRslLibraryDefinition? library = null;
        if (_renderStates is not null && !_renderStates.TryGetLibrary(rslId, out library!))
        {
            return new EnbBaseAssetResolution(
                selector,
                EnbBaseAssetResolutionStatus.MissingRslLibrary,
                definition,
                Diagnostic: $"BASE-{selector} RSLID {rslId} is not available in the loaded rs.ini catalog.");
        }

        EnbResolvedRslLibraryResourceGraph? resourceGraph =
            library is null ? null : _rslResources.Resolve(library);

        return new EnbBaseAssetResolution(
            selector,
            EnbBaseAssetResolutionStatus.RslReference,
            definition,
            Rsl: new EnbResolvedRslReference(
                rslId,
                definition.LinkId,
                definition.RsId,
                library,
                EnbRslRenderStateSemantics.BuildInitialCommands(definition.LinkId, definition.RsId),
                resourceGraph));
    }

    private EnbBaseAssetResolution ResolveW3d(
        int selector,
        EnbBaseAssetDefinition definition,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? registeredPrototypes)
    {
        string? file = definition.File?.Trim();
        string? objectName = definition.Name?.Trim();

        if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(objectName))
        {
            return new EnbBaseAssetResolution(
                selector,
                EnbBaseAssetResolutionStatus.UnsupportedDefinition,
                definition,
                Diagnostic: $"BASE-{selector} selected FILE/NAME without both authored values.");
        }

        //006F54AE's authored FILE reaches the same0091D220/004E57A0
        //provider as lazy names: successful loose opens precede packages.
        byte[] bytes = [];
        EnbMixResourceLocation location = null!;
        string? loosePath = null;
        bool found;
        if (_files is not null)
        {
            found = _files.TryRead(file, out bytes, out var package, out loosePath);
            if (found) location = package ?? new(file, loosePath!, 0, bytes.Length);
        }
        else found = _resources is not null && _resources.TryReadResource(file, out bytes, out location);
        if (!found)
        {
            return new EnbBaseAssetResolution(
                selector,
                EnbBaseAssetResolutionStatus.MissingResource,
                definition,
                Diagnostic: $"BASE-{selector} W3D resource '{file}' is not available in the installed MIX archives.");
        }

        if (!EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveAuthoredResource(
                _resources,
                file,
                objectName,
                bytes,
                location,
                out EnbResolvedW3dResource resolved,
                out string? w3dDiagnostic, _files, loosePath, registeredPrototypes))
        {
            return new EnbBaseAssetResolution(
                selector,
                EnbBaseAssetResolutionStatus.InvalidW3d,
                definition,
                Diagnostic: $"BASE-{selector} resource '{location.Name}' is not a supported/valid W3D model: {w3dDiagnostic ?? "unknown decoder rejection"}");
        }

        return new EnbBaseAssetResolution(
            selector,
            EnbBaseAssetResolutionStatus.W3d,
            definition,
            W3d: resolved);
    }
}
