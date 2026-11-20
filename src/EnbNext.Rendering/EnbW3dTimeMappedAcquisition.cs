using EnbNext.Assets;
using System.Runtime.CompilerServices;

namespace EnbNext.Rendering;

/// <summary>0091DA00/0091DC30 and 00964980: recursive, first-name-owned
/// HAnim acquisition. Construction precedes duplicate publication.</summary>
public sealed class EnbW3dTimeMappedAcquisition
{
    private readonly Dictionary<string, EnbW3dRenderHierarchyAnimation> _animations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,string> _sources=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _hierarchies = new(StringComparer.OrdinalIgnoreCase);
    // Remember operation identity without owning duplicate decoded FILEs.
    private readonly ConditionalWeakTable<EnbW3dModel, object> _published = new();
    private sealed class RetainedPublication
    {
        internal readonly string Provenance;
        internal readonly List<(EnbW3dRenderHierarchyAnimation Animation,string Key)> Pending=[];
        internal RetainedPublication(EnbW3dRenderResource resource)
        {
            Provenance="retained:"+resource.LogicalFileName;
            foreach(var animation in resource.Animations)
                if(animation.TimeMappedDefinition is not null)Pending.Add((animation,EnbW3dTimeMappedAcquisition.Key(animation)));
        }
    }
    private readonly ConditionalWeakTable<EnbW3dRenderResource,RetainedPublication> _retainedPublications=new();
    public long RetainedPublicationCount {get;private set;}
    public bool HasPendingDependencies(EnbW3dRenderResource resource)
        =>_retainedPublications.TryGetValue(resource,out var state)&&state.Pending.Count!=0;
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    private EnbInstalledAssetPaths? _paths;
    private EnbMixResourceCatalog? _resources, _packages;
    public int Count => _animations.Count;
    public int ConstructedCount { get; private set; }
    public int DuplicateCount { get; private set; }
    public string? Diagnostic { get; private set; }
    public string? SourceOf(string name)=>_sources.GetValueOrDefault(name);

    public EnbW3dTimeMappedAcquisition() { }
    public EnbW3dTimeMappedAcquisition(EnbInstalledAssetPaths paths, EnbMixResourceCatalog? resources, EnbMixResourceCatalog? packages)
    { _paths = paths; _resources = resources; _packages = packages; }

    public void Clear()
    {
        _retainedPublications.Clear();RetainedPublicationCount=0;
        _animations.Clear(); _sources.Clear(); _hierarchies.Clear(); _published.Clear(); _loading.Clear(); _missing.Clear();
        _paths = null; _resources = null; _packages = null;
        ConstructedCount = DuplicateCount = 0; Diagnostic = null;
    }

    public EnbW3dRenderHierarchyAnimation? Resolve(EnbW3dRenderResource retained,
        EnbResolvedRslFileDependency file, EnbInstalledAssetPaths? paths, EnbMixResourceCatalog? resources,
        EnbMixResourceCatalog? packages, string name)
    {
        _paths = paths; _resources = resources; _packages = packages;
        // Replaying raw roots preserves FILE ordering when that decoded FILE is
        // available. Otherwise the retained resource is the loaded publication.
        bool sameFile = file.Model is not null && file.ResourceLocation is {} location && EnbW3dRenderResourceKey.From(location) == retained.Key;
        RetainedPublication? publication=null;
        if (!sameFile && !_retainedPublications.TryGetValue(retained,out publication))
        {
            // A compiled resource is a FILE publication, not a frame command.
            // Register its immutable ordinary definitions once. The weak value
            // holds only definitions, never the resource key itself.
            publication=new(retained);_retainedPublications.Add(retained,publication);RetainedPublicationCount++;
            foreach (var hierarchy in retained.Hierarchies) _hierarchies.TryAdd(hierarchy.Name, hierarchy.Pivots.Count);
            foreach (var animation in retained.Animations)
                if (animation.Family != EnbW3dHAnimFamily.Adaptive02C0)
                    Register(animation,publication.Provenance);
        }
        if (file.Model is {} model && !_published.TryGetValue(model,out _)) Publish(model,"FILE:"+file.LogicalFileName);
        // Preserve acquisition order and retries for incomplete adaptive
        // dependencies. Successfully published definitions leave this list.
        if(publication is not null)
            for(int i=0;i<publication.Pending.Count;)
            {
                var pending=publication.Pending[i];
                if(!_animations.ContainsKey(pending.Key))
                    Construct(pending.Animation,pending.Animation.TimeMappedDefinition!,publication.Provenance);
                if(_animations.ContainsKey(pending.Key))publication.Pending.RemoveAt(i);
                else i++;
            }
        return FindAnimation(name);
    }

    public void Publish(EnbW3dModel model,string provenance="FILE")
    {
        if (_published.TryGetValue(model,out _)) return;
        _published.Add(model,new object());
        var entries = model.Hierarchies.Select(h => (Offset:h.LoadOffset, Hierarchy:(EnbW3dHierarchy?)h, Animation:(EnbW3dHierarchyAnimation?)null))
            .Concat(model.Animations.Select(a => (Offset:a.LoadOffset, Hierarchy:(EnbW3dHierarchy?)null, Animation:(EnbW3dHierarchyAnimation?)a)))
            .OrderBy(e => e.Offset);
        foreach (var entry in entries)
        {
            if (entry.Hierarchy is {} h)
            {
                if (EnbW3dRenderResourceCompiler.TryCompileHierarchy(h, out var compiled, out _))
                    _hierarchies.TryAdd(h.Name, compiled.Pivots.Count);
                continue;
            }
            if (!EnbW3dRenderResourceCompiler.TryCompileAnimation(0, entry.Animation!, out var animation, out _)) continue;
            if (animation.TimeMappedDefinition is {} definition) Construct(animation, definition,provenance);
            else Register(animation,provenance);
        }
    }

    private bool Register(EnbW3dRenderHierarchyAnimation animation,string provenance)
    {
        string name=Key(animation);
        if(!_animations.TryAdd(name,animation))return false;
        _sources.Add(name,provenance);return true;
    }

    private void Construct(EnbW3dRenderHierarchyAnimation animation, EnbW3dTimeMappedAnimation definition,string provenance)
    {
        if (!_hierarchies.TryGetValue(animation.HierarchyName, out int pivots))
        {
            LoadWithParent(animation.HierarchyName + ".w3d");
            if (!_hierarchies.TryGetValue(animation.HierarchyName, out pivots))
            { Diagnostic = "02C0 hierarchy acquisition failed: " + animation.HierarchyName; return; }
        }
        // Incomplete native reference arrays contain heap residue. Contain that
        // storage defect, without imposing a new limit on valid source counts.
        if (definition.SourceCount != definition.Sources.Count)
        { Diagnostic = "02C0 source count does not describe initialized reference storage."; return; }
        var sources = definition.Sources.Select(source => FindAnimation(source.Name)).ToArray();
        try
        {
            var playback = new EnbW3dTimeMappedPlayback(definition, pivots, sources);
            var acquired = animation.Copy().SetTimeMappedPlayback(playback);
            ConstructedCount++;
            if (!Register(acquired,provenance)) DuplicateCount++;
        }
        catch (InvalidDataException error) { Diagnostic = error.Message; }
    }

    private EnbW3dRenderHierarchyAnimation? FindAnimation(string name)
    {
        if (_animations.TryGetValue(name, out var animation)) return animation;
        if (_missing.Contains(name)) return null;
        if (EnbCurrentRetailRslHAnimResolver.TryBuildLazyResourceName(name, out string file)) LoadWithParent(file);
        if (_animations.TryGetValue(name, out animation)) return animation;
        _missing.Add(name);
        return null;
    }

    private void LoadWithParent(string file)
    {
        if (!Load(file)) Load(@"..\" + file);
    }

    private bool Load(string file)
    {
        // Native cycles recurse indefinitely. Contain only the active read,
        // without manufacturing a replacement clip or source transform.
        if (!_loading.Add(file)) { Diagnostic = "Cyclic 02C0 FILE acquisition: " + file; return true; }
        try
        {
            if (!EnbCurrentRetailRslHAnimResolver.TryReadCurrentRetailResource(_paths,_resources,_packages,file,
                    out var bytes,out var provenance,out var error))
            { Diagnostic = error; return error is not null; }
            if (EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var decoded,out var diagnostic)) Publish(decoded.Model,$"lazy:{file}@{provenance}");
            else Diagnostic = diagnostic;
            //0091D270 opening the FILE succeeds independently of child loaders.
            return true;
        }
        finally { _loading.Remove(file); }
    }

    private static string Key(EnbW3dRenderHierarchyAnimation animation)
        => EnbCurrentRetailRslHAnimResolver.BuildRegistryName(animation.Name, animation.HierarchyName);
}
