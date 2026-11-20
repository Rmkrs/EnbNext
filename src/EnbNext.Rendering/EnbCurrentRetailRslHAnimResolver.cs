using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>
/// One HAnim selected with the bounded current-retail WW3D asset-manager lookup
/// order used by RSL Animation states. Geometry remains owned by the retained
/// RenderObj resource; an HAnim loaded from another W3D is appended only to the
/// immutable draw view used for hierarchy deformation.
/// </summary>
public sealed record EnbCurrentRetailRslHAnimResolution(
    string HAnimName,
    string Source,
    EnbW3dRenderHierarchyAnimation Animation,
    EnbW3dRenderResource DrawResource)
{
    public bool RequiresAcquisitionMaintenance {get;internal set;}
    public ulong RegistryGeneration {get;internal set;}
}

/// <summary>
/// Header-only evidence for a current-retail lazy HAnim resource that could not
/// be promoted through the currently executable W3D animation boundary. This is
/// diagnostic evidence, not a draw resolution.
/// </summary>
public sealed record EnbCurrentRetailRslHAnimHeaderResolution(
    string HAnimName,
    string Source,
    EnbW3dHAnimHeaderInfo Header);

/// <summary>
/// Current-retail RSL HAnim lookup boundary recovered through R6Z20.
///
/// WW3DAssetManager::Get_HAnim first checks the already-loaded global HAnim
/// registry. If the requested dotted HAnim is absent and lazy loading is
/// enabled, retail takes the substring after the first '.', appends ".w3d",
/// and asks the current composite resource manager to load it. That manager
/// resolves the installed loose-file search path first and MIX packages second.
/// If and only if that first W3D load fails, Get_HAnim retries once with the
/// literal "..\\" prefix, then retries the original HAnim name in the registry.
/// </summary>
public static class EnbCurrentRetailRslHAnimResolver
{
    /// <summary>
    /// Current retail does not insert the raw W3D animation-header Name into
    /// the global HAnim registry. The 0x0200/0x0280/0x02C0 loaders build the
    /// virtual Get_Name() value as &lt;HierarchyName&gt; + "." + &lt;Name&gt; and the
    /// registry compares that composed identity. No "already qualified" special
    /// case exists in those loaders.
    /// </summary>
    public static string BuildRegistryName(string animationName, string hierarchyName)
    {
        ArgumentNullException.ThrowIfNull(animationName);
        ArgumentNullException.ThrowIfNull(hierarchyName);
        return hierarchyName + "." + animationName;
    }

    public static bool TryBuildLazyResourceName(
        string hAnimName,
        out string logicalFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hAnimName);
        logicalFileName = string.Empty;

        int separator = hAnimName.IndexOf('.');
        if (separator < 0)
            return false;

        logicalFileName = hAnimName[(separator + 1)..] + ".w3d";
        return true;
    }

    public static bool TryBuildLazyResourceCandidates(
        string hAnimName,
        out string primaryLogicalFileName,
        out string parentLogicalFileName)
    {
        parentLogicalFileName = string.Empty;
        if (!TryBuildLazyResourceName(hAnimName, out primaryLogicalFileName))
            return false;

        parentLogicalFileName = @"..\" + primaryLogicalFileName;
        return true;
    }

    /// <summary>
    /// R6Z20 evidence-only companion to TryResolve. It follows the same exact
    /// current-retail primary/parent lazy resource order but stops after a raw
    /// 0x0200/0x0280/0x02C0 header proves the requested registry identity. A
    /// returned header is intentionally not treated as executable animation.
    /// </summary>
    public static bool TryProbeLazyHeader(
        EnbInstalledAssetPaths? installedPaths,
        EnbMixResourceCatalog? resources,
        EnbMixResourceCatalog? retailPackageResources,
        string hAnimName,
        out EnbCurrentRetailRslHAnimHeaderResolution resolution,
        out string? diagnostic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hAnimName);
        resolution = default!;
        diagnostic = null;

        if (!TryBuildLazyResourceCandidates(
                hAnimName,
                out string primaryLogicalFileName,
                out string parentLogicalFileName))
        {
            diagnostic = $"HAnim '{hAnimName}' has no current-retail lazy W3D filename branch.";
            return false;
        }

        LazyHeaderCandidateResult primary = TryProbeLazyHeaderCandidate(
            installedPaths,
            resources,
            retailPackageResources,
            primaryLogicalFileName,
            hAnimName,
            out resolution);
        if (primary.Disposition == LazyCandidateDisposition.Resolved)
            return true;
        if (primary.Disposition != LazyCandidateDisposition.NotFound)
        {
            diagnostic = primary.Diagnostic;
            return false;
        }

        LazyHeaderCandidateResult parent = TryProbeLazyHeaderCandidate(
            installedPaths,
            resources,
            retailPackageResources,
            parentLogicalFileName,
            hAnimName,
            out resolution);
        if (parent.Disposition == LazyCandidateDisposition.Resolved)
            return true;
        diagnostic = parent.Diagnostic;
        return false;
    }

    /// <summary>
    /// Compatibility overload for callers/tests that have only a MIX catalog.
    /// R6Z15 live execution should supply the selected installed paths so loose
    /// current-retail overrides participate before the package fallback.
    /// </summary>
    public static bool TryResolve(
        EnbW3dRenderResource retainedResource,
        EnbResolvedRslFileDependency authoredFile,
        EnbMixResourceCatalog? resources,
        string hAnimName,
        out EnbCurrentRetailRslHAnimResolution resolution,
        out string? diagnostic)
        => TryResolve(
            retainedResource,
            authoredFile,
            null,
            resources,
            null,
            hAnimName,
            out resolution,
            out diagnostic);

    public static bool TryResolve(
        EnbW3dRenderResource retainedResource,
        EnbResolvedRslFileDependency authoredFile,
        EnbInstalledAssetPaths? installedPaths,
        EnbMixResourceCatalog? resources,
        string hAnimName,
        out EnbCurrentRetailRslHAnimResolution resolution,
        out string? diagnostic)
        => TryResolve(
            retainedResource,
            authoredFile,
            installedPaths,
            resources,
            null,
            hAnimName,
            out resolution,
            out diagnostic);

    public static bool TryResolve(
        EnbW3dRenderResource retainedResource,
        EnbResolvedRslFileDependency authoredFile,
        EnbInstalledAssetPaths? installedPaths,
        EnbMixResourceCatalog? resources,
        EnbMixResourceCatalog? retailPackageResources,
        string hAnimName,
        out EnbCurrentRetailRslHAnimResolution resolution,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(retainedResource);
        ArgumentNullException.ThrowIfNull(authoredFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(hAnimName);

        resolution = default!;
        diagnostic = null;

        EnbW3dRenderHierarchyAnimation[] retainedMatches = retainedResource.Animations
            .Where(candidate => string.Equals(
                BuildRegistryName(candidate.Name, candidate.HierarchyName),
                hAnimName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var mappedOwner = new EnbW3dTimeMappedAcquisition();
        var mapped = mappedOwner.Resolve(retainedResource, authoredFile, installedPaths, resources, retailPackageResources, hAnimName);
        if (mapped is not null)
        {
            resolution = new(hAnimName, mappedOwner.SourceOf(hAnimName)!, mapped,
                retainedResource.Copy().SetAnimations(retainedResource.Animations.Where(a =>
                    !BuildRegistryName(a.Name,a.HierarchyName).Equals(hAnimName,StringComparison.OrdinalIgnoreCase)).Append(mapped).ToArray()));
            return true;
        }
        if (retainedMatches.Any(a => a.Family == EnbW3dHAnimFamily.Adaptive02C0))
        { diagnostic = mappedOwner.Diagnostic ?? "02C0 acquisition failed."; return false; }
        if (retainedMatches.Length > 1)
        {
            diagnostic =
                $"retained resource '{retainedResource.LogicalFileName}' contains {retainedMatches.Length} HAnim entries named '{hAnimName}'; current asset-manager registry winner is ambiguous.";
            return false;
        }

        EnbW3dRenderHierarchyAnimation? fileAnimation = null;
        bool samePhysicalResource = authoredFile.ResourceLocation is EnbMixResourceLocation fileLocation &&
            EnbW3dRenderResourceKey.From(fileLocation) == retainedResource.Key;
        if (!samePhysicalResource && authoredFile.Model is EnbW3dModel fileModel)
        {
            if (!TryCompileUniqueAnimation(
                    fileModel,
                    hAnimName,
                    $"authored FILE '{authoredFile.LogicalFileName}'",
                    out fileAnimation,
                    out diagnostic))
            {
                return false;
            }
        }

        EnbW3dRenderHierarchyAnimation? retainedAnimation = retainedMatches.SingleOrDefault();
        if (retainedAnimation is not null && fileAnimation is not null)
        {
            diagnostic =
                $"HAnim '{hAnimName}' exists in both retained RenderObj resource '{retainedResource.LogicalFileName}' and authored FILE '{authoredFile.LogicalFileName}'; current global registry duplicate precedence is not proven.";
            return false;
        }

        if (retainedAnimation is not null)
        {
            resolution = new EnbCurrentRetailRslHAnimResolution(
                hAnimName,
                $"retained:{retainedResource.LogicalFileName}",
                retainedAnimation,
                retainedResource);
            return true;
        }

        if (fileAnimation is not null)
        {
            resolution = new EnbCurrentRetailRslHAnimResolution(
                hAnimName,
                $"FILE:{authoredFile.LogicalFileName}",
                fileAnimation,
                AppendAnimation(retainedResource, fileAnimation));
            return true;
        }

        if (!TryBuildLazyResourceCandidates(
                hAnimName,
                out string primaryLazyLogicalName,
                out string parentLazyLogicalName))
        {
            diagnostic =
                $"HAnim '{hAnimName}' is not already loaded and has no '.' separator, so current-retail Get_HAnim has no lazy W3D filename branch.";
            return false;
        }

        LazyCandidateResult primary = TryLoadLazyCandidate(
            retainedResource,
            installedPaths,
            resources,
            retailPackageResources,
            primaryLazyLogicalName,
            hAnimName,
            out resolution);
        if (primary.Disposition == LazyCandidateDisposition.Resolved)
            return true;
        if (primary.Disposition != LazyCandidateDisposition.NotFound)
        {
            diagnostic = primary.Diagnostic;
            return false;
        }

        LazyCandidateResult parent = TryLoadLazyCandidate(
            retainedResource,
            installedPaths,
            resources,
            retailPackageResources,
            parentLazyLogicalName,
            hAnimName,
            out resolution);
        if (parent.Disposition == LazyCandidateDisposition.Resolved)
            return true;
        if (parent.Disposition != LazyCandidateDisposition.NotFound)
        {
            diagnostic = parent.Diagnostic;
            return false;
        }

        string packageCensus = retailPackageResources is null
            ? string.Empty
            : $" Retail package census {retailPackageResources.ArchiveCount}/{retailPackageResources.DiscoveredArchiveCount} accepted/discovered.";
        string retainedRegistry = DescribeRegistry(retainedResource.Animations);
        diagnostic =
            $"current-retail Get_HAnim found no modeled already-loaded HAnim named '{hAnimName}' and lazy loader found neither '{primaryLazyLogicalName}' nor second-pass '{parentLazyLogicalName}' through the installed loose-file search path followed by the complete *.MIX package namespace.{packageCensus} Retained resource '{retainedResource.LogicalFileName}' contributes {retainedRegistry}.";
        return false;
    }

    private static LazyHeaderCandidateResult TryProbeLazyHeaderCandidate(
        EnbInstalledAssetPaths? installedPaths,
        EnbMixResourceCatalog? resources,
        EnbMixResourceCatalog? retailPackageResources,
        string logicalFileName,
        string hAnimName,
        out EnbCurrentRetailRslHAnimHeaderResolution resolution)
    {
        resolution = default!;

        if (!TryReadCurrentRetailResource(
                installedPaths,
                resources,
                retailPackageResources,
                logicalFileName,
                out byte[] bytes,
                out string provenance,
                out string? resourceDiagnostic))
        {
            return resourceDiagnostic is null
                ? new LazyHeaderCandidateResult(LazyCandidateDisposition.NotFound, null)
                : new LazyHeaderCandidateResult(LazyCandidateDisposition.LoadedButUnresolved, resourceDiagnostic);
        }

        if (!EnbW3dHAnimHeaderProbe.TryInspect(
                bytes,
                out IReadOnlyList<EnbW3dHAnimHeaderInfo> headers,
                out string? probeDiagnostic))
        {
            return new LazyHeaderCandidateResult(
                LazyCandidateDisposition.LoadedButUnresolved,
                $"current-retail lazy candidate '{logicalFileName}' resolved from {provenance}, but raw HAnim-family census failed: {probeDiagnostic ?? "unknown header rejection"}.");
        }

        EnbW3dHAnimHeaderInfo[] matches = headers
            .Where(header => string.Equals(header.RegistryName, hAnimName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length > 1)
        {
            return new LazyHeaderCandidateResult(
                LazyCandidateDisposition.LoadedButUnresolved,
                $"current-retail lazy candidate '{logicalFileName}' from {provenance} contains {matches.Length} raw HAnim headers with registry key '{hAnimName}'; winner is ambiguous.");
        }

        if (matches.Length == 0)
        {
            return new LazyHeaderCandidateResult(
                LazyCandidateDisposition.LoadedButUnresolved,
                $"current-retail lazy candidate '{logicalFileName}' resolved from {provenance} but its {headers.Count} raw HAnim header(s) do not contain registry key '{hAnimName}'. Retail does not enter the '..\' retry after a successful primary W3D load; R6Z20 does not infer later loader success from header absence.");
        }

        EnbW3dHAnimHeaderInfo header = matches[0];
        resolution = new EnbCurrentRetailRslHAnimHeaderResolution(
            hAnimName,
            $"lazy-header:{logicalFileName}@{provenance}",
            header);
        return new LazyHeaderCandidateResult(LazyCandidateDisposition.Resolved, null);
    }

    private static LazyCandidateResult TryLoadLazyCandidate(
        EnbW3dRenderResource retainedResource,
        EnbInstalledAssetPaths? installedPaths,
        EnbMixResourceCatalog? resources,
        EnbMixResourceCatalog? retailPackageResources,
        string logicalFileName,
        string hAnimName,
        out EnbCurrentRetailRslHAnimResolution resolution)
    {
        resolution = default!;

        if (!TryReadCurrentRetailResource(
                installedPaths,
                resources,
                retailPackageResources,
                logicalFileName,
                out byte[] bytes,
                out string provenance,
                out string? resourceDiagnostic))
        {
            return resourceDiagnostic is null
                ? new LazyCandidateResult(LazyCandidateDisposition.NotFound, null)
                : new LazyCandidateResult(LazyCandidateDisposition.LoadedButUnresolved, resourceDiagnostic);
        }

        if (!EnbW3dModel.TryDecode(bytes, out EnbW3dModel model))
        {
            return new LazyCandidateResult(
                LazyCandidateDisposition.LoadedButUnresolved,
                $"current-retail lazy candidate '{logicalFileName}' resolved from {provenance}, but the current EnbNext W3D boundary cannot decode it. Retail's loader success/failure cannot be inferred safely, so R6Z15 does not skip to another candidate.");
        }

        if (!TryCompileUniqueAnimation(
                model,
                hAnimName,
                $"current-retail lazy candidate '{logicalFileName}' from {provenance}",
                out EnbW3dRenderHierarchyAnimation? animation,
                out string? compileDiagnostic))
        {
            return new LazyCandidateResult(
                LazyCandidateDisposition.LoadedButUnresolved,
                compileDiagnostic);
        }

        if (animation is null)
        {
            return new LazyCandidateResult(
                LazyCandidateDisposition.LoadedButUnresolved,
                $"current-retail lazy candidate '{logicalFileName}' loaded from {provenance} but did not register HAnim '{hAnimName}'. Retail does not enter the '..\\' retry after a successful W3D load.");
        }

        resolution = new EnbCurrentRetailRslHAnimResolution(
            hAnimName,
            $"lazy:{logicalFileName}@{provenance}",
            animation,
            AppendAnimation(retainedResource, animation));
        return new LazyCandidateResult(LazyCandidateDisposition.Resolved, null);
    }

    internal static bool TryReadCurrentRetailResource(
        EnbInstalledAssetPaths? installedPaths,
        EnbMixResourceCatalog? resources,
        EnbMixResourceCatalog? retailPackageResources,
        string logicalFileName,
        out byte[] bytes,
        out string provenance,
        out string? diagnostic)
    {
        diagnostic = null;
        if (installedPaths is not null)
        {
            string filesystemLogicalName = logicalFileName
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            if (installedPaths.TryResolveRetailLooseResource(filesystemLogicalName, out string path))
            {
                try
                {
                    bytes = File.ReadAllBytes(EnbNext.Installation.EnbPathCase.Resolve(path));
                    string relativePath = Path.GetRelativePath(installedPaths.RootDirectory, path);
                    provenance = $"loose:{relativePath}";
                    return true;
                }
                catch (IOException)
                {
                    // Retail's composite manager can fall through to MIX when the
                    // loose candidate cannot actually be opened.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same open-failure behavior as above.
                }
            }
        }

        if (retailPackageResources is not null)
        {
            if (retailPackageResources.TryGetResourceLocations(
                    logicalFileName,
                    out IReadOnlyList<EnbMixResourceLocation> packageLocations))
            {
                // Retail 004E5440 keeps the first enumerated package for a duplicate name.
                EnbMixResourceLocation uniqueLocation = packageLocations[0];
                if (retailPackageResources.TryReadResource(uniqueLocation, out bytes))
                {
                    provenance = $"MIX:{uniqueLocation.ArchiveName}/{uniqueLocation.Name}";
                    return true;
                }

                bytes = Array.Empty<byte>();
                provenance = string.Empty;
                diagnostic =
                    $"current-retail package lookup located '{logicalFileName}' in '{packageLocations[0].ArchiveName}', but the resource payload could not be read.";
                return false;
            }

            bytes = Array.Empty<byte>();
            provenance = string.Empty;
            return false;
        }

        if (resources is not null &&
            resources.TryReadResource(
                logicalFileName,
                out bytes,
                out EnbMixResourceLocation mixLocation))
        {
            provenance = $"MIX:{mixLocation.ArchiveName}/{mixLocation.Name}";
            return true;
        }

        bytes = Array.Empty<byte>();
        provenance = string.Empty;
        return false;
    }

    private static bool TryCompileUniqueAnimation(
        EnbW3dModel model,
        string hAnimName,
        string sourceDescription,
        out EnbW3dRenderHierarchyAnimation? animation,
        out string? diagnostic)
    {
        animation = null;
        diagnostic = null;

        int[] ordinals = model.Animations
            .Select((candidate, ordinal) => (candidate, ordinal))
            .Where(pair => string.Equals(
                BuildRegistryName(pair.candidate.Name, pair.candidate.HierarchyName),
                hAnimName,
                StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.ordinal)
            .ToArray();
        if (ordinals.Length > 1)
        {
            diagnostic =
                $"{sourceDescription} contains {ordinals.Length} HAnim entries named '{hAnimName}'; current asset-manager registry winner is ambiguous.";
            return false;
        }
        if (ordinals.Length == 0)
            return true;

        int ordinal = ordinals[0];
        if (model.Animations[ordinal].Family == EnbW3dHAnimFamily.Adaptive02C0)
        { diagnostic = "02C0 source acquisition did not produce an executable owner."; return false; }
        if (!EnbW3dRenderResourceCompiler.TryCompileAnimation(
                ordinal,
                model.Animations[ordinal],
                out EnbW3dRenderHierarchyAnimation compiled,
                out string? compileDiagnostic))
        {
            diagnostic =
                $"{sourceDescription} HAnim '{hAnimName}' could not compile: {compileDiagnostic ?? "unknown animation rejection"}.";
            return false;
        }

        animation = compiled;
        return true;
    }

    private static EnbW3dRenderResource AppendAnimation(
        EnbW3dRenderResource retainedResource,
        EnbW3dRenderHierarchyAnimation animation)
        => retainedResource.Copy().SetAnimations(retainedResource.Animations
                .Concat([animation])
                .ToArray());

    private static string DescribeRegistry(IReadOnlyList<EnbW3dRenderHierarchyAnimation> animations)
    {
        const int maximumNames = 8;
        if (animations.Count == 0)
            return "0 executable HAnim(s)";

        string names = string.Join(
            ", ",
            animations
                .Take(maximumNames)
                .Select(static animation => BuildRegistryName(animation.Name, animation.HierarchyName)));
        if (animations.Count > maximumNames)
            names += $", +{animations.Count - maximumNames}";
        return $"{animations.Count} executable HAnim(s) [{names}]";
    }

    private enum LazyCandidateDisposition
    {
        NotFound,
        LoadedButUnresolved,
        Resolved,
    }

    private readonly record struct LazyCandidateResult(
        LazyCandidateDisposition Disposition,
        string? Diagnostic);

    private readonly record struct LazyHeaderCandidateResult(
        LazyCandidateDisposition Disposition,
        string? Diagnostic);
}
