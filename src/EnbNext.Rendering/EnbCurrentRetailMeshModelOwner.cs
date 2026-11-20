namespace EnbNext.Rendering;

/// <summary>
/// Mesh model/mapper identity lifetime for batching. RenderObj copy0093EA50
/// shares the model;00940300 copies it only when shared or explicitly forced.
/// Runtime mapper owners are copied with their class-specific state semantics;
/// Explicit optional geometry follows the base-copy/vector separation paths;
/// descriptor arrays remain with the caller. Loaded material keys
/// identify table entries across copies; MaterialIdentity returns their current
/// material objects, which must be used when remapping a copied descriptor.
/// </summary>
public sealed class EnbCurrentRetailMeshModelOwner : IDisposable
{
    private sealed record MaterialEntry(object Key, object Material, object?[] Mappers);
    private sealed class Model
    {
        public int References = 1;
        public object? PreparedRegistration;
        public uint MaterialIdentityRevision;
        public List<MaterialEntry> Materials { get; } = [];
        public List<object> Textures { get; } = [];
        public List<EnbCurrentRetailTextureRegistry.Lease> TextureLeases { get; } = [];
        public EnbCurrentRetailMeshGeometryState? Geometry;
        public EnbCurrentRetailMeshColorBufferOwner? ColorBuffer0,ColorBuffer1;
    }

    private Model? _model;
    private readonly Action<object>? _retire;
    internal sealed class MaterialLocalWord
    {
        private uint _value;
        internal event Action? Changed;
        public uint Value
        {
            get => _value;
            set { if (_value == value) return; _value = value; Changed?.Invoke(); }
        }
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object,MaterialLocalWord> MaterialLocalWords=new();
    private static readonly object MaterialLocalIdGate = new();
    private static uint _nextMaterialLocalId = 1; // Current binary00BBAEF8.

    public EnbCurrentRetailMeshModelOwner(IEnumerable<(object Material, object? Mapper0, object? Mapper1)> materials,
        Action<object>? retire = null, IEnumerable<object>? loadedTextures = null,EnbCurrentRetailMeshGeometryState? geometry=null,
        EnbCurrentRetailMeshColorBufferOwner? colorBuffer0=null,EnbCurrentRetailMeshColorBufferOwner? colorBuffer1=null)
    {
        _model = new();
        _retire = retire;
        _model.Geometry=geometry;
        _model.ColorBuffer0=colorBuffer0?.Retain();_model.ColorBuffer1=colorBuffer1?.Retain();
        // The caller supplies the loader's table, not a union of descriptor
        // bindings.00951D50 appends entries without deduplicating them.
        if (loadedTextures is not null)
            foreach (var texture in loadedTextures)
            {
                if (texture is EnbCurrentRetailTextureRegistry.Lease lease)
                {
                    var retained = lease.Retain();
                    _model.TextureLeases.Add(retained);
                    _model.Textures.Add(retained.State);
                }
                else _model.Textures.Add(texture);
            }
        foreach (var material in materials)
            _model.Materials.Add(new(material.Material, material.Material, [material.Mapper0, material.Mapper1]));
    }

    private EnbCurrentRetailMeshModelOwner(Model model, Action<object>? retire)
    {
        _model = model;
        _retire = retire;
        model.References++;
    }

    private Model Current => _model ?? throw new ObjectDisposedException(nameof(EnbCurrentRetailMeshModelOwner));
    public object ModelIdentity => Current;
    internal object? PreparedRegistration { get => Current.PreparedRegistration; set => Current.PreparedRegistration = value; }
    public uint MaterialIdentityRevision => Current.MaterialIdentityRevision;
    public EnbCurrentRetailMeshGeometryState? Geometry=>Current.Geometry;
    public EnbCurrentRetailMeshColorBufferOwner? ColorBuffer0=>Current.ColorBuffer0;
    public EnbCurrentRetailMeshColorBufferOwner? ColorBuffer1=>Current.ColorBuffer1;
    public EnbCurrentRetailMeshModelOwner CloneRenderObject() => new(Current, _retire);
    private MaterialEntry LoadedMaterial(object key)
        => Current.Materials.FirstOrDefault(entry => ReferenceEquals(entry.Key, key))
            ?? throw new KeyNotFoundException("Material is not in this model's loaded table.");
    public object? MapperIdentity(object material, int stage) => LoadedMaterial(material).Mappers[stage];
    public object MaterialIdentity(object material) => LoadedMaterial(material).Material;
    public int LoadedMaterialCount => Current.Materials.Count;
    public object MaterialIdentityAt(int index) => Current.Materials[index].Material;
    public object? MapperIdentityAt(int index, int stage) => Current.Materials[index].Mappers[stage];
    /// <summary>00939750 with localization disabled (RObj activation006EF200).
    /// Scan the loaded table for a time-variant mapper before resetting every
    /// non-null mapper in table order. Duplicate pointers are visited again;
    /// descriptor-only materials do not participate. Ownership is unchanged.</summary>
    public bool ResetLoadedMappers(uint activationMilliseconds)
    {
        var materials = Current.Materials;
        if (!materials.Any(entry => entry.Mappers.Any(mapper =>
                mapper is EnbW3dMapperRuntimeState { Program.UsesElapsedTime: true })))
            return false;
        foreach (var entry in materials)
            foreach (var mapper in entry.Mappers)
                if (mapper is EnbW3dMapperRuntimeState runtime)
                    runtime.Reset(activationMilliseconds);
        return true;
    }
    public uint MaterialRuntimeWord64At(int index)
        => MaterialRuntimeWord64(Current.Materials[index].Material);
    public static uint MaterialRuntimeWord64(object material)
        => MaterialLocalWords.TryGetValue(material,out var word)?word.Value:0;
    internal static MaterialLocalWord RetainMaterialRuntimeWord64(object material)
        => MaterialLocalWords.GetOrCreateValue(material);
    internal static void PreserveMaterialRuntimeWord64(object source,object destination)
    {
        if(MaterialLocalWords.TryGetValue(source,out var word))
            MaterialLocalWords.GetOrCreateValue(destination).Value=word.Value;
    }

    /// <summary>0048BCF0→0092EB20 visits the loaded table in order, including
    /// duplicate pointers. The supplied global counter advances once per entry;
    /// every reference to the same material observes its final assigned word.</summary>
    public void MarkLoadedMaterialsInstanceLocal(ref uint nextMaterialId)
    {
        Current.MaterialIdentityRevision++;
        foreach(var entry in Current.Materials)
        {
            MaterialLocalWords.GetOrCreateValue(entry.Material).Value=nextMaterialId;
            nextMaterialId=unchecked(nextMaterialId+1);
        }
    }
    public void MarkLoadedMaterialsInstanceLocal()
    {
        lock (MaterialLocalIdGate) MarkLoadedMaterialsInstanceLocal(ref _nextMaterialLocalId);
    }
    /// <summary>Concrete0048BCF0: unique model first, HSV material IDs then
    /// primary color buffers0/1, then unconditional vector separation for scale.</summary>
    public void PrepareConfiguredMesh(bool changesScale,bool changesHsv,uint creationMilliseconds,ref uint nextMaterialId)
    {
        EnsureUniqueModel(creationMilliseconds);
        if(changesHsv)
        {
            MarkLoadedMaterialsInstanceLocal(ref nextMaterialId);
            Current.ColorBuffer0?.EnsureUnique();Current.ColorBuffer1?.EnsureUnique();
        }
        if(changesScale)Current.Geometry?.SeparateVectorStorage();
    }
    public int LoadedTextureCount => Current.Textures.Count;
    public object TextureIdentity(int index) => Current.Textures[index];

    /// <summary>
    /// 00951EC0/00952080 use pointer membership in the loaded texture table.
    /// An empty table leaves descriptor references alone. Otherwise a foreign
    /// reference becomes null, even if its name or resource values match.
    /// Apply to active uniform slots or expanded entries only.
    /// </summary>
    public object? RemapTextureTo(EnbCurrentRetailMeshModelOwner copy, object? texture)
    {
        var source = Current;
        var destination = copy.Current;
        if (source.Textures.Count == 0) return texture;
        if (texture is null) return null;
        for (var i = 0; i < source.Textures.Count; i++)
            if (ReferenceEquals(source.Textures[i], texture))
                return destination.Textures[i];
        return null;
    }

    /// <summary>
    /// 00951EC0/00952080: remap a descriptor reference from this loaded table
    /// to a copied model. An empty source table skips remapping; a nonempty
    /// table maps null or foreign references to null. Call only for active
    /// uniform slots or expanded entries, not hidden uniform slots.
    /// </summary>
    public object? RemapMaterialTo(EnbCurrentRetailMeshModelOwner copy, object? material)
    {
        var source = Current;
        var destination = copy.Current;
        if (source.Materials.Count == 0) return material;
        if (material is null) return null;
        for (int i = 0; i < source.Materials.Count; i++)
            if (ReferenceEquals(source.Materials[i].Material, material))
                return destination.Materials[i].Material;
        return null;
    }

    public void EnsureUniqueModel(uint creationMilliseconds, bool force = false)
    {
        var old = Current;
        if (old.References == 1 && !force) return;
        var copy = new Model();
        copy.Geometry=old.Geometry?.CopyModel();
        copy.ColorBuffer0=old.ColorBuffer0?.Retain();copy.ColorBuffer1=old.ColorBuffer1?.Retain();
        //00951B60 retains texture objects, preserving table order and duplicates.
        copy.Textures.AddRange(old.Textures);
        foreach (var lease in old.TextureLeases) copy.TextureLeases.Add(lease.Retain());
        //00951B60/0092EB90 copy each material and each non-null mapper separately.
        foreach (var entry in old.Materials)
        {
            if(entry.Material is null) { copy.Materials.Add(new(entry.Key,null!,[null,null])); continue; }
            var mappers=entry.Mappers.Select(mapper => mapper switch
            {
                null => null,
                EnbW3dMapperRuntimeState runtime => (object)runtime.CloneForMaterial(creationMilliseconds),
                _ => new object() // Identity-only owners used by batch-layout callers.
            }).ToArray();
            var material = entry.Material is EnbW3dRenderVertexMaterial value ? value with
            {
                CurrentRetailMapper0Owner=mappers[0] is EnbW3dMapperRuntimeState stage0?new(stage0):null,
                CurrentRetailMapper1Owner=mappers[1] is EnbW3dMapperRuntimeState stage1?new(stage1):null,
            } : (object)new object();
            PreserveMaterialRuntimeWord64(entry.Material,material);
            copy.Materials.Add(new(entry.Key, material,mappers));
        }
        _model = copy;
        Release(old);
    }

    /// <summary>Keep model/texture leases alive through queued submission even
    /// when the RenderObj is removed. The caller drains each token exactly once.</summary>
    public QueuedReference RetainQueued() => new(this);
    public struct QueuedReference : IDisposable
    {
        private EnbCurrentRetailMeshModelOwner? _owner;
        private readonly Model _model;
        internal QueuedReference(EnbCurrentRetailMeshModelOwner owner)
        { _owner = owner; _model = owner.Current; _model.References++; }
        public void Dispose()
        {
            var owner = _owner; _owner = null;
            if (owner is not null) owner.Release(_model);
        }
    }

    private void Release(Model model)
    {
        if (--model.References == 0)
        {
            foreach (var lease in model.TextureLeases) lease.Dispose();
            model.ColorBuffer0?.Dispose();model.ColorBuffer1?.Dispose();
            _retire?.Invoke(model);
        }
    }

    public void Dispose()
    {
        if (_model is not { } model) return;
        _model = null;
        Release(model);
    }
}
