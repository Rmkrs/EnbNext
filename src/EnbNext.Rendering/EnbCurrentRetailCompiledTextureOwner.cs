using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>
/// Replays loader texture acquisitions of a freshly compiled mesh and
/// attaches its shared objects to authored descriptor references. The caller
/// resolves native name keys and creation capabilities and owns publication
/// order. Runtime-replaced descriptors must not be passed as authored input.
/// LoadedTextures retains the native table's order and duplicate entries.
/// </summary>
public sealed class EnbCurrentRetailCompiledTextureOwner : IDisposable
{
    private readonly List<EnbCurrentRetailTextureRegistry.Lease> _leases = [];
    private bool _disposed;
    public EnbW3dRenderMesh Mesh { get; private set; }
    internal void SetPublicationOrdinal(int ordinal)=>Mesh=(Mesh).Copy().SetOrdinal(ordinal);
    internal void SetLoadedGeometry(EnbCurrentRetailMeshGeometryState geometry)=>Mesh=(Mesh).Copy().SetCurrentRetailLoadedGeometry(geometry);
    public IReadOnlyList<EnbCurrentRetailTextureRegistry.Lease> LoadedTextures { get; }

    public EnbCurrentRetailCompiledTextureOwner(EnbW3dRenderMesh mesh,
        EnbCurrentRetailTextureRegistry registry,
        Func<EnbW3dTextureAcquisition, (string Key, EnbW3dTextureSamplingSemantics Creation)> resolve)
        : this(mesh, registry, definition =>
        {
            var resolved = resolve(definition);
            return (resolved.Key, new EnbW3dTextureCreationRequest(resolved.Creation, 0));
        }) { }

    public EnbCurrentRetailCompiledTextureOwner(EnbW3dRenderMesh mesh,
        EnbCurrentRetailTextureRegistry registry,
        Func<EnbW3dTextureAcquisition, (string Key, EnbW3dTextureCreationRequest Creation)> resolve)
        :this(mesh,registry,resolve,null) { }

    public EnbCurrentRetailCompiledTextureOwner(EnbW3dRenderMesh mesh,
        EnbCurrentRetailTextureRegistry registry,
        Func<EnbW3dTextureAcquisition, (string Key, EnbW3dTextureCreationRequest Creation)> resolve,
        Action<string,EnbCurrentRetailTextureObjectState>? initializeColdTexture)
    {
        var acquisitions = new List<EnbCurrentRetailTextureRegistry.Lease>();
        try
        {
            var acquiredNames = new Dictionary<string, EnbCurrentRetailTextureRegistry.Lease>(StringComparer.Ordinal);
            foreach (var definition in mesh.TextureAcquisitions)
            {
                var acquisition = resolve(definition);
                var lease = registry.Acquire(acquisition.Key, acquisition.Creation, definition.Info,out bool created);
                acquisitions.Add(lease);
                if(created)initializeColdTexture?.Invoke(definition.LogicalName,lease.State);
                acquiredNames[definition.LogicalName] = lease;
            }
            // Retaining the final table must not replay info writes. Earlier
            // discarded legacy lookups can establish an object's creation state.
            foreach (var name in mesh.CurrentRetailLoadedTextureNames)
                _leases.Add(acquiredNames[name].Retain());
            var bindings = new Dictionary<EnbW3dRenderTextureBinding, EnbW3dRenderTextureBinding>(ReferenceEqualityComparer.Instance);
            EnbW3dRenderTextureBinding? Bind(EnbW3dRenderTextureBinding? binding)
            {
                if (binding is null || binding.IsUnbound ||
                    binding.SourceKind is not (EnbW3dRenderBindingSourceKind.ModernAuthored or EnbW3dRenderBindingSourceKind.LegacyRoot))
                    return binding;
                if (!bindings.TryGetValue(binding, out var retained))
                {
                    var name = binding.SourceKind == EnbW3dRenderBindingSourceKind.LegacyRoot
                        ? mesh.LegacyTextureNames[checked((int)binding.SourceId)]
                        : mesh.ModernTextureDefinitions[checked((int)binding.SourceId)].LogicalName;
                    retained = binding with { CurrentRetailTextureState = acquiredNames[name].State };
                    bindings.Add(binding, retained);
                }
                return retained;
            }
            var textureLists = new EnbW3dTextureBindingListPool();
            IReadOnlyList<EnbW3dRenderTextureBinding?> BindTextures(IReadOnlyList<EnbW3dRenderTextureBinding?> source)
                => source.Count<=2
                    ? textureLists.Get(source.Count,source.Count>0?Bind(source[0]):null,source.Count>1?Bind(source[1]):null)
                    : textureLists.Freeze(source.Select(Bind).ToArray());
            Mesh = (mesh).Copy().SetCurrentRetailLoadedTextureStates(Array.AsReadOnly(_leases.Select(lease=>lease.State).ToArray())).SetMaterialPasses(Array.AsReadOnly(mesh.MaterialPasses.Select(pass => (pass).Copy().SetTextureStages(Array.AsReadOnly(pass.TextureStages.Select(stage => (stage).Copy().SetTextures(Array.AsReadOnly(stage.Textures.Select(texture => Bind(texture)!).ToArray())).SetCurrentRetailUniformTexture(Bind(stage.CurrentRetailUniformTexture))).ToArray())).SetTriangleBindings(Array.AsReadOnly(pass.TriangleBindings.Select(triangle => triangle with
                    {
                        Textures = BindTextures(triangle.Textures),
                    }).ToArray()))).ToArray()));
            LoadedTextures = _leases.AsReadOnly();
        }
        catch
        {
            foreach (var lease in _leases) lease.Dispose();
            throw;
        }
        finally
        {
            foreach (var lease in acquisitions) lease.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var lease in _leases) lease.Dispose();
    }
}
