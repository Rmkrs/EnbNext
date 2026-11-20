using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>
/// Persistent current-retail HAnim registry view for one selected asset namespace.
///
/// Retail WW3DAssetManager::Get_HAnim first consults a process-owned registry and
/// only enters the lazy W3D loader when the requested composed HAnim identity is
/// absent. The live EnbNext renderer previously replayed that lazy resource read,
/// W3D decode and animation compilation on every presentation frame. Besides being
/// expensive, that was less faithful than retaining the successful registry result.
///
/// The owner must Clear() this runtime when the selected retail asset namespace is
/// replaced. Asset lookup failures and header probes are namespace-owned too:
/// repeating disk searches at presentation cadence does not produce new evidence.
/// A new registration invalidates failed resolutions; Clear invalidates everything.
/// Corpus-audit completion is evaluated separately by the caller, not cached here.
/// </summary>
public sealed class EnbCurrentRetailRslHAnimRegistryRuntime
{
    private readonly EnbW3dTimeMappedAcquisition _timeMapped = new();
    private readonly Dictionary<RegistryKey, EnbCurrentRetailRslHAnimResolution> _resolved = [];
    private readonly Dictionary<RegistryKey, string?> _failures = [];
    private readonly Dictionary<string, (EnbCurrentRetailRslHAnimHeaderResolution? Resolution, string? Diagnostic)> _headers = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _resolved.Count;
    public int RegistrationCount => _timeMapped.Count;
    public bool HasPendingAcquisition(EnbW3dRenderResource resource) => _timeMapped.HasPendingDependencies(resource);
    public ulong Generation {get;private set;}

    public ulong HitCount { get; private set; }

    public ulong MissCount { get; private set; }

    public void Clear()
    {
        Generation++;
        _timeMapped.Clear();
        _resolved.Clear();
        _failures.Clear();
        _headers.Clear();
        HitCount = 0;
        MissCount = 0;
    }

    public bool TryResolve(
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

        // A new FILE publication still constructs its duplicate before lookup;
        // repeated presentation of the same decoded FILE is not a new load.
        var mapped = _timeMapped.Resolve(retainedResource, authoredFile, installedPaths, resources, retailPackageResources, hAnimName);
        RegistryKey key = RegistryKey.Create(retainedResource, authoredFile, hAnimName);
        if (_resolved.TryGetValue(key, out EnbCurrentRetailRslHAnimResolution? cached))
        {
            HitCount++;
            cached.RequiresAcquisitionMaintenance=_timeMapped.HasPendingDependencies(retainedResource);
            resolution = cached;
            diagnostic = null;
            return true;
        }

        if (mapped is null && _failures.TryGetValue(key, out diagnostic))
        {
            HitCount++;
            resolution = default!;
            return false;
        }

        MissCount++;
        if (mapped is not null)
        {
            //964930 dispatches all three families into the same name table.
            //964A30/964AE0 discard duplicates just like964980. A classic or
            //compressed winner must not fall through to per-FILE selection.
            resolution = new(hAnimName, _timeMapped.SourceOf(hAnimName)!, mapped,
                retainedResource.Copy().SetAnimations(retainedResource.Animations.Where(a =>
                    !EnbCurrentRetailRslHAnimResolver.BuildRegistryName(a.Name,a.HierarchyName).Equals(hAnimName,StringComparison.OrdinalIgnoreCase))
                    .Append(mapped).ToArray()));
            resolution.RegistryGeneration=Generation;
            resolution.RequiresAcquisitionMaintenance=_timeMapped.HasPendingDependencies(retainedResource);
            _resolved[key] = resolution; _failures.Clear(); diagnostic = null; return true;
        }
        // 91DA5A/91DA61 returns a remembered miss before another lazy read.
        // A second, stateless resolver would bypass that namespace-owned miss
        // whenever a different draw owner requests the same animation name.
        diagnostic = _timeMapped.Diagnostic ?? $"HAnim '{hAnimName}' was not registered by FILE or lazy acquisition.";
        _failures[key] = diagnostic;
        resolution = default!;
        return false;
    }

    public bool TryProbeLazyHeader(
        EnbInstalledAssetPaths? installedPaths,
        EnbMixResourceCatalog? resources,
        EnbMixResourceCatalog? retailPackageResources,
        string hAnimName,
        out EnbCurrentRetailRslHAnimHeaderResolution resolution,
        out string? diagnostic)
    {
        if (!_headers.TryGetValue(hAnimName, out var cached))
        {
            bool found = EnbCurrentRetailRslHAnimResolver.TryProbeLazyHeader(
                installedPaths, resources, retailPackageResources, hAnimName,
                out var header, out diagnostic);
            cached = (found ? header : null, diagnostic);
            _headers[hAnimName] = cached;
        }
        resolution = cached.Resolution!;
        diagnostic = cached.Diagnostic;
        return resolution is not null;
    }

    private readonly record struct RegistryKey(
        EnbW3dRenderResourceKey RetainedResource,
        string AuthoredFileLogicalName,
        string? AuthoredArchiveName,
        long AuthoredOffset,
        long AuthoredLength,
        string HAnimName)
    {
        public static RegistryKey Create(
            EnbW3dRenderResource retainedResource,
            EnbResolvedRslFileDependency authoredFile,
            string hAnimName)
        {
            EnbMixResourceLocation? location = authoredFile.ResourceLocation;
            return new RegistryKey(
                retainedResource.Key,
                authoredFile.LogicalFileName,
                location?.ArchiveName,
                location?.DataOffset ?? 0L,
                location?.DataLength ?? 0L,
                hAnimName.Trim());
        }
    }
}
