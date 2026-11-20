using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>
/// Retains post-load ship material mutations, not animated packets. The caller
/// owns one installed asset namespace and clears on reload. Material packet
/// revisions invalidate replacements without advancing object visual generation.
/// EndFrame may retain owners held by the caller's bounded GPU residency cache.
/// </summary>
public sealed class EnbShipMaterialCache
{
    private readonly Stage<SurfaceResult> _surfaces = new();
    private readonly Stage<SwatchResult> _swatches = new();
    private readonly Stage<EnbCurrentRetailShipNameDecalReceipt> _names = new();
    private readonly Dictionary<EnbW3dRenderMesh, Dictionary<Key, SurfaceMutation>> _surfaceMutations =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(ulong Identity,ulong Generation)> _persistentOwners=[];
    private readonly Dictionary<(ulong Identity,ulong Generation),HashSet<SurfaceMutation>> _ownerMutations=[];
    private readonly HashSet<SurfaceMutation> _temporaryMutations=[];
    private readonly List<SurfaceMutation> _retiredMutations=[];
    public int LastSweepCandidateCount {get;private set;}
    public long MarkSeenWrites=>_surfaces.MarkSeenWrites+_names.MarkSeenWrites+_swatches.MarkSeenWrites+_mutationMarkSeenWrites;
    private long _mutationMarkSeenWrites;
    public void RetainOwner((ulong Identity,ulong Generation) owner)
    {
        if(!_persistentOwners.Add(owner))return;
        _surfaces.RetainOwner(owner);_names.RetainOwner(owner);_swatches.RetainOwner(owner);
        if(_ownerMutations.TryGetValue(owner,out var mutations))foreach(var mutation in mutations)
        {mutation.Persistent=true;_temporaryMutations.Remove(mutation);}
    }
    public void ReleaseOwner((ulong Identity,ulong Generation) owner)
    {
        _persistentOwners.Remove(owner);
        _surfaces.ReleaseOwner(owner);_names.ReleaseOwner(owner);_swatches.ReleaseOwner(owner);
        if(_ownerMutations.TryGetValue(owner,out var mutations))foreach(var mutation in mutations.ToArray())RemoveMutation(mutation);
    }
    private void RemoveMutation(SurfaceMutation mutation)
    {
        mutation.Active=false;_temporaryMutations.Remove(mutation);
        var owner=(mutation.Key.Identity,mutation.Key.Generation);
        if(_ownerMutations.TryGetValue(owner,out var owned)){owned.Remove(mutation);if(owned.Count==0)_ownerMutations.Remove(owner);}
        if(_surfaceMutations.TryGetValue(mutation.Mesh,out var entries)&&entries.TryGetValue(mutation.Key,out var current)&&ReferenceEquals(current,mutation))
        {entries.Remove(mutation.Key);if(entries.Count==0)_surfaceMutations.Remove(mutation.Mesh);}
    }
    private long _frame;
    private long _bindingEpoch;
    public long DirectSurfaceUses {get;private set;}
    public long DirectNameUses {get;private set;}
    public long NameKeyDerivations {get;private set;}
    public long SurfaceKeyDerivations {get;private set;}
    public long SwatchKeyDerivations {get;private set;}
    public int Hits => _surfaces.Hits + _names.Hits;
    public int Misses => _surfaces.Misses + _names.Misses;
    public void Clear() { _bindingEpoch++; _surfaces.Clear(); _names.Clear(); _swatches.Clear(); _surfaceMutations.Clear();_ownerMutations.Clear();_temporaryMutations.Clear(); }
    public void BeginFrame() { _frame++; _surfaces.BeginFrame(); _names.BeginFrame(); _swatches.BeginFrame(); }
    public void EndFrame(IReadOnlySet<(ulong Identity, ulong Generation)>? residentOwners = null)
    {
        _surfaces.EndFrame(residentOwners); _names.EndFrame(residentOwners); _swatches.EndFrame(residentOwners);
        LastSweepCandidateCount=_surfaces.LastSweepCandidateCount+_names.LastSweepCandidateCount+_swatches.LastSweepCandidateCount;
        _retiredMutations.Clear();
        foreach(var mutation in _temporaryMutations)
        {
            LastSweepCandidateCount++;
            if(mutation.Frame!=_frame&&residentOwners?.Contains((mutation.Key.Identity,mutation.Key.Generation))!=true)_retiredMutations.Add(mutation);
        }
        foreach(var mutation in _retiredMutations)RemoveMutation(mutation);
        _retiredMutations.Clear();
    }

    /// <summary>A material binding owned by one prepared draw occurrence. It
    /// carries no pose or clock input; queued frames keep their own values.</summary>
    public sealed class SurfaceBinding
    {
        public readonly NameBinding Name=new();
        public long PreparationCount {get;internal set;}
        internal EnbShipMaterialCache? Owner;
        internal long Epoch;
        internal EnbW3dMeshDescriptor? Input,Output;
        internal EnbW3dDrawMaterialBinding? BaseMaterial;
        internal string? Path;
        internal int Region,ColorCount,DecalCount;
        internal EnbEnvironmentMapAxis? Axis;
        internal bool ApplyColor;
        internal EnbMetalEquivalentRgbCatalog? Metals;
        internal EnbDecalDefinitionCatalog? Decals;
        internal EnbCurrentRetailSwatchCatalog? RetainedDecals;
        internal Stage<SurfaceResult>.Entry? Entry;
        internal SurfaceMutation? Mutation;
    }

    public EnbW3dDrawTask ApplySurface(EnbW3dDrawTask packet, IEnbRenderObject obj,
        string path, int region, EnbEnvironmentMapAxis? axis, bool applyColor,
        EnbMetalEquivalentRgbCatalog? metals, EnbDecalDefinitionCatalog? decals,
        out SurfaceResult receipt,EnbCurrentRetailSwatchCatalog? retainedDecals=null,
        EnbW3dDrawMaterialBinding? preparedBaseMaterial=null,SurfaceBinding? binding=null)
    {
        int colorCount=obj.ColorizationAudit?.ApplyCount??0;
        if(binding is not null && ReferenceEquals(binding.Owner,this) && binding.Epoch==_bindingEpoch &&
            ReferenceEquals(binding.Input,packet.PreparedState) && ReferenceEquals(binding.BaseMaterial,preparedBaseMaterial) &&
            binding.Path==path && binding.Region==region && binding.Axis==axis && binding.ApplyColor==applyColor &&
            binding.ColorCount==colorCount && binding.DecalCount==obj.DecalApplyCount &&
            ReferenceEquals(binding.Metals,metals) && ReferenceEquals(binding.Decals,decals) &&
            ReferenceEquals(binding.RetainedDecals,retainedDecals) && binding.Entry is {Active:true} entry &&
            binding.Mutation is {Active:true} mutation)
        {
            if(!mutation.Persistent){mutation.Frame=_frame;_mutationMarkSeenWrites++;}_surfaces.Touch(entry);DirectSurfaceUses++;
            receipt=entry.Receipt;
            return packet.WithPreparedState(binding.Output!);
        }
        var result=ApplySurfaceCore(packet,obj,path,region,axis,applyColor,metals,decals,out receipt,
            retainedDecals,preparedBaseMaterial,out var usedEntry,out var usedMutation);
        if(binding is not null)
        {
            binding.PreparationCount++;
            binding.Owner=this;binding.Epoch=_bindingEpoch;binding.Input=packet.PreparedState;binding.Output=result.PreparedState;
            binding.BaseMaterial=preparedBaseMaterial;binding.Path=path;binding.Region=region;binding.Axis=axis;
            binding.ApplyColor=applyColor;binding.ColorCount=colorCount;binding.DecalCount=obj.DecalApplyCount;
            binding.Metals=metals;binding.Decals=decals;binding.RetainedDecals=retainedDecals;
            binding.Entry=usedEntry;binding.Mutation=usedMutation;
        }
        return result;
    }

    private EnbW3dDrawTask ApplySurfaceCore(EnbW3dDrawTask packet, IEnbRenderObject obj,
        string path, int region, EnbEnvironmentMapAxis? axis, bool applyColor,
        EnbMetalEquivalentRgbCatalog? metals, EnbDecalDefinitionCatalog? decals,
        out SurfaceResult receipt,EnbCurrentRetailSwatchCatalog? retainedDecals,
        EnbW3dDrawMaterialBinding? preparedBaseMaterial,out Stage<SurfaceResult>.Entry usedEntry,out SurfaceMutation usedMutation)
    {
        SurfaceKeyDerivations++;
        var key = new Key(packet.RenderIdentity, packet.VisualGeneration, packet.RenderObjectName,
            path, region, axis, applyColor, metals is not null, decals is not null, new(packet.Mesh));
        var source = (preparedBaseMaterial ?? packet.Material).Source;
        // 00812390 creates and installs new mapper objects for each applicable
        // colorization, including identical bytes. Keep that event separate
        // from VisualGeneration, which also owns retained RSL animation state.
        int revision = applyColor && EnbCurrentRetailShipColorizationRuntime.HasColorizationMaterialBindings(source)
            ? obj.ColorizationAudit?.ApplyCount ?? 0 : 0;
        // 00735DF0 -> 0080E8C0 -> 0080EAF0 replaces matching DECALdd
        // descriptors on repeated packets, independently of RGB changes.
        int decalRevision = EnbCurrentRetailShipDecalRuntime.TryParseSlot(packet.RenderObjectName, out _)
            ? obj.DecalApplyCount : 0;
        long surfaceRevision = ((long)(uint)(applyColor ? obj.ColorizationAudit?.ApplyCount ?? 0 : 0) << 32) | (uint)decalRevision;
        // 00812390 and 0080F470 mutate one model's materials. Descriptor
        // passes referring to the same object share the replacement and its
        // mapper owner. Separate mesh instances and update events do not.
        if (!_surfaceMutations.TryGetValue(packet.Mesh, out var mutations))
            _surfaceMutations.Add(packet.Mesh, mutations = []);
        long mutationRevision = ((long)(uint)(applyColor ? obj.ColorizationAudit?.ApplyCount ?? 0 : 0) << 32) | (uint)decalRevision;
        if (!mutations.TryGetValue(key, out var mutation) || mutation.Revision != mutationRevision)
        {
            if(mutation is not null)RemoveMutation(mutation);
            // Removal can unlink the last entry for this mesh; restore its map.
            _surfaceMutations[packet.Mesh]=mutations;
            mutations[key] = mutation = new(mutationRevision,packet.Mesh,key);
            var owner=(key.Identity,key.Generation);
            mutation.Persistent=_persistentOwners.Contains(owner);
            if(!_ownerMutations.TryGetValue(owner,out var owned))_ownerMutations.Add(owner,owned=[]);
            owned.Add(mutation);if(!mutation.Persistent)_temporaryMutations.Add(mutation);
        }
        if(!mutation.Persistent){mutation.Frame=_frame;_mutationMarkSeenWrites++;}usedMutation=mutation;
        if (_surfaces.TryGet(source, key, out var material, out receipt, out var loadedMaterials, out var loadedTextures, out var preparedEntry, surfaceRevision))
        {
            usedEntry=preparedEntry!;
            if (packet.Material == material && ReferenceEquals(packet.CurrentRetailLoadedMaterials, loadedMaterials) &&
                ReferenceEquals(packet.CurrentRetailLoadedTextures, loadedTextures) &&
                packet.CurrentRetailColorizationRevision == (receipt.Color.Applied ? revision : 0) &&
                packet.CurrentRetailDecalRevision == (receipt.Decal.Applied ? decalRevision : 0)) return packet;
            if(ReferenceEquals(preparedEntry!.PreparedInput,packet.PreparedState))
                return packet.WithPreparedState(preparedEntry.PreparedOutput!);
            var result=(packet).ForkTask().SetMaterial(material).SetCurrentRetailLoadedMaterials(loadedMaterials).SetCurrentRetailLoadedTextures(loadedTextures).SetCurrentRetailColorizationRevision(receipt.Color.Applied ? revision : 0).SetCurrentRetailDecalRevision(receipt.Decal.Applied ? decalRevision : 0);
            preparedEntry.PreparedInput=packet.PreparedState;preparedEntry.PreparedOutput=result.PreparedState;
            return result;
        }
        if (preparedBaseMaterial is not null && !ReferenceEquals(packet.Material, preparedBaseMaterial))
            packet = (packet).ForkTask().SetMaterial(preparedBaseMaterial);
        packet = EnbCurrentRetailShipColorizationRuntime.Apply(packet, obj.ColorizationEntries,
            region, axis, applyColor, mutation.Color, out var color);
        packet = EnbCurrentRetailWeaponMetalColorRuntime.Apply(packet, path, obj.ColorizationEntries,
            region, metals, mutation.Metal, mutation.Color, out var metal);
        packet = EnbCurrentRetailShipDecalRuntime.Apply(packet, obj.Decals, decals, out var decal,retainedDecals,
            hasDecalUpdate:obj.DecalApplyCount!=0);
        receipt = new(color, metal, decal);
        usedEntry=_surfaces.Store(source, key, packet.Material, receipt, surfaceRevision, packet.CurrentRetailLoadedMaterials,packet.CurrentRetailLoadedTextures);
        return (packet).ForkTask().SetCurrentRetailColorizationRevision(color.Applied ? revision : 0).SetCurrentRetailDecalRevision(decal.Applied ? decalRevision : 0);
    }

    public bool TryApplySwatch(IReadOnlyList<EnbW3dDrawTask> target,
        EnbCurrentRetailSwatchDescriptor descriptor, int revision,
        out IReadOnlyList<EnbW3dDrawTask> packets, out string? diagnostic)
    {
        packets = Array.Empty<EnbW3dDrawTask>();
        diagnostic = null;
        if (target.Count == 0) return true;
        var first = target[0];
        int bindingBase = first.OrderKey.BindingOrdinal - first.Material.Source.Ordinal;
        SwatchKeyDerivations++;
        var key = new Key(first.RenderIdentity, first.VisualGeneration, first.RenderObjectName,
            first.RenderInstancePath, bindingBase, null, false, false, false, new(first.Mesh));
        var source = first.Material.Source;
        if (!_swatches.TryGet(source, key, out _, out var cached, out _, out _, out _, revision) ||
            !ReferenceEquals(cached.Descriptor, descriptor) ||
            cached.Sources.Count != target.Count ||
            cached.Sources.Where((pass, index) => !ReferenceEquals(pass, target[index].Material.Source)).Any())
        {
            if (!descriptor.TryApply(target, revision, out packets, out diagnostic)) return false;
            foreach(var task in packets)task.RetainAfterDrain();
            cached = new(descriptor, target.Select(p => p.Material.Source).ToArray(), packets);
            // Zero-pass descriptors have no GPU owner, but remain a valid
            // descriptor replacement. Use the target binding as the cache slot.
            _swatches.Store(source, key, first.Material, cached, revision);
            return true;
        }
        packets = cached.Packets.Select(template =>
        {
            var current = target.FirstOrDefault(p => p.Material.Source.Ordinal == template.Material.Source.Ordinal) ?? first;
            return (current).ForkTask().SetMaterial(template.Material).SetCurrentRetailLoadedMaterials(template.CurrentRetailLoadedMaterials).SetCurrentRetailLoadedTextures(template.CurrentRetailLoadedTextures).SetCurrentRetailSharedMaterialMappers(template.CurrentRetailSharedMaterialMappers).SetCurrentRetailDecalRevision(revision).SetOrderKey(current.OrderKey with
                {
                    MaterialPassOrdinal = template.OrderKey.MaterialPassOrdinal,
                    BindingOrdinal = template.OrderKey.BindingOrdinal,
                    BlendClass = template.OrderKey.BlendClass,
                });
        }).ToArray();
        return true;
    }

    private sealed record SwatchResult(EnbCurrentRetailSwatchDescriptor Descriptor,
        IReadOnlyList<EnbW3dRenderMaterialPass> Sources, IReadOnlyList<EnbW3dDrawTask> Packets);
    public sealed class NameBinding
    {
        public long PreparationCount {get;internal set;}
        internal EnbShipMaterialCache? Owner;
        internal long Epoch;
        internal EnbW3dMeshDescriptor? Input,Output;
        internal EnbSceneNameDecalSnapshot? Name;
        internal EnbCurrentRetailShipNameDecalTexture? Texture;
        internal int NameCount,ColorCount,Region;
        internal EnbEnvironmentMapAxis? Axis;
        internal bool LaterColor;
        internal Stage<EnbCurrentRetailShipNameDecalReceipt>.Entry? Entry;
    }
    public EnbW3dDrawTask ApplyName(EnbW3dDrawTask packet, IEnbRenderObject obj,
        EnbCurrentRetailShipNameDecalTexture? texture, int region, EnbEnvironmentMapAxis? axis,
        bool applyColor, out EnbCurrentRetailShipNameDecalReceipt receipt,NameBinding? binding=null)
    {
        bool laterColor=applyColor && obj.NameDecalColorizationApplyCount is int before &&
            (obj.ColorizationAudit?.ApplyCount ?? 0)>before;
        int colorCount=laterColor?obj.ColorizationAudit!.ApplyCount:0;
        if(binding is not null && ReferenceEquals(binding.Owner,this) && binding.Epoch==_bindingEpoch &&
            ReferenceEquals(binding.Input,packet.PreparedState) && ReferenceEquals(binding.Name,obj.NameDecal) &&
            ReferenceEquals(binding.Texture,texture) && binding.NameCount==obj.NameDecalApplyCount &&
            binding.LaterColor==laterColor && binding.ColorCount==colorCount && binding.Region==region && binding.Axis==axis &&
            binding.Entry is {Active:true} entry)
        {
            _names.Touch(entry);DirectNameUses++;receipt=entry.Receipt;
            return packet.WithPreparedState(binding.Output!);
        }
        var result=ApplyNameCore(packet,obj.NameDecal,texture,out receipt,obj.NameDecalApplyCount,
            laterColor?obj:null,region,axis,out var usedEntry);
        if(binding is not null)
        {
            binding.PreparationCount++;binding.Owner=this;binding.Epoch=_bindingEpoch;
            binding.Input=packet.PreparedState;binding.Output=result.PreparedState;binding.Name=obj.NameDecal;
            binding.Texture=texture;binding.NameCount=obj.NameDecalApplyCount;binding.LaterColor=laterColor;
            binding.ColorCount=colorCount;binding.Region=region;binding.Axis=axis;binding.Entry=usedEntry;
        }
        return result;
    }

    public EnbW3dDrawTask ApplyName(EnbW3dDrawTask packet, EnbSceneNameDecalSnapshot? name,
        EnbCurrentRetailShipNameDecalTexture? texture, out EnbCurrentRetailShipNameDecalReceipt receipt,
        int applyCount = 0)
        => ApplyNameCore(packet, name, texture, out receipt, applyCount, null, 0, null,out _);

    private EnbW3dDrawTask ApplyNameCore(EnbW3dDrawTask packet, EnbSceneNameDecalSnapshot? name,
        EnbCurrentRetailShipNameDecalTexture? texture, out EnbCurrentRetailShipNameDecalReceipt receipt,
        int applyCount, IEnbRenderObject? laterColor, int region, EnbEnvironmentMapAxis? axis,
        out Stage<EnbCurrentRetailShipNameDecalReceipt>.Entry usedEntry)
    {
        NameKeyDerivations++;
        var key = new Key(packet.RenderIdentity, packet.VisualGeneration, packet.RenderObjectName,
            "", region, axis, laterColor is not null, texture is not null, false, new(packet.Mesh));
        var source = packet.Material.Source;
        int revision = EnbCurrentRetailShipNameDecalRuntime.IsTarget(packet.RenderObjectName) ? applyCount : 0;
        long cacheRevision = ((long)(uint)revision << 32) | (uint)(laterColor?.ColorizationAudit?.ApplyCount ?? 0);
        if (_names.TryGet(source, key, out var material, out receipt, out var loadedMaterials, out var loadedTextures, out var preparedEntry, cacheRevision))
        {
            usedEntry=preparedEntry!;
            if (packet.Material == material && ReferenceEquals(packet.CurrentRetailLoadedMaterials, loadedMaterials) &&
                ReferenceEquals(packet.CurrentRetailLoadedTextures, loadedTextures) &&
                packet.CurrentRetailNameDecalRevision == (receipt.Applied ? revision : 0)) return packet;
            if(ReferenceEquals(preparedEntry!.PreparedInput,packet.PreparedState))
                return packet.WithPreparedState(preparedEntry.PreparedOutput!);
            var result=(packet).ForkTask().SetMaterial(material).SetCurrentRetailLoadedMaterials(loadedMaterials).SetCurrentRetailLoadedTextures(loadedTextures).SetCurrentRetailNameDecalRevision(receipt.Applied ? revision : 0);
            preparedEntry.PreparedInput=packet.PreparedState;preparedEntry.PreparedOutput=result.PreparedState;
            return result;
        }
        packet = EnbCurrentRetailShipNameDecalRuntime.Apply(packet, name, texture, out receipt);
        // 00735FC0/00810130 writes the existing uniform material's RGB and
        // shader. A subsequent 00735ED0/00812390 color packet overwrites those
        // fields on the resulting one-entry loaded table, retaining the name
        // texture. Snapshot projection must not move that name write later.
        if (receipt.Applied && laterColor is not null)
            packet = EnbCurrentRetailShipColorizationRuntime.Apply(packet, laterColor.ColorizationEntries,
                region, axis, out _);
        usedEntry=_names.Store(source, key, packet.Material, receipt, cacheRevision, packet.CurrentRetailLoadedMaterials,packet.CurrentRetailLoadedTextures);
        return (packet).ForkTask().SetCurrentRetailNameDecalRevision(receipt.Applied ? revision : 0);
    }

    public sealed record SurfaceResult(EnbCurrentRetailShipColorizationReceipt Color,
        EnbCurrentRetailWeaponMetalColorReceipt Metal, EnbCurrentRetailShipDecalReceipt Decal);

    internal readonly record struct Key(ulong Identity, ulong Generation, string RenderObject,
        string Path, int Region, EnbEnvironmentMapAxis? Axis, bool ApplyColor, bool Available0, bool Available1, MeshKey Mesh);

    internal readonly record struct MeshKey(EnbW3dRenderMesh Mesh)
    {
        public bool Equals(MeshKey other) => ReferenceEquals(Mesh, other.Mesh);
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Mesh);
    }

    internal sealed class SurfaceMutation(long revision,EnbW3dRenderMesh mesh,Key key)
    {
        public bool Active=true,Persistent;
        public readonly EnbW3dRenderMesh Mesh=mesh;
        public readonly Key Key=key;
        public long Revision { get; } = revision;
        public long Frame;
        public Dictionary<EnbW3dRenderVertexMaterial, EnbW3dRenderVertexMaterial> Color { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<EnbW3dRenderVertexMaterial, EnbW3dRenderVertexMaterial> Metal { get; } = new(ReferenceEqualityComparer.Instance);
    }

    internal sealed class Stage<T>
    {
        private readonly Dictionary<EnbW3dRenderMaterialPass,Dictionary<Key,Entry>> _passes=new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(ulong Identity,ulong Generation),HashSet<Entry>> _owners=[];
        private readonly HashSet<(ulong Identity,ulong Generation)> _persistent=[];
        private readonly HashSet<Entry> _temporary=[];
        private readonly List<Entry> _retire=[];
        private long _frame;
        public int Hits {get;private set;}
        public int Misses {get;private set;}
        public int LastSweepCandidateCount {get;private set;}
        public long MarkSeenWrites {get;private set;}
        public void Clear(){_passes.Clear();_owners.Clear();_temporary.Clear();_retire.Clear();}
        public void BeginFrame(){_frame++;Hits=Misses=0;}
        public void RetainOwner((ulong Identity,ulong Generation) owner)
        {
            _persistent.Add(owner);
            if(_owners.TryGetValue(owner,out var entries))foreach(var entry in entries)
            {entry.Persistent=true;_temporary.Remove(entry);}
        }
        public void ReleaseOwner((ulong Identity,ulong Generation) owner)
        {
            _persistent.Remove(owner);
            if(_owners.TryGetValue(owner,out var entries))foreach(var entry in entries.ToArray())Remove(entry);
        }
        private void Remove(Entry entry)
        {
            entry.Active=false;_temporary.Remove(entry);
            var owner=(entry.Key.Identity,entry.Key.Generation);
            if(_owners.TryGetValue(owner,out var owned)){owned.Remove(entry);if(owned.Count==0)_owners.Remove(owner);}
            if(_passes.TryGetValue(entry.Source,out var entries)&&entries.TryGetValue(entry.Key,out var current)&&ReferenceEquals(current,entry))
            {entries.Remove(entry.Key);if(entries.Count==0)_passes.Remove(entry.Source);}
        }
        public bool TryGet(EnbW3dRenderMaterialPass source,Key key,
            out EnbW3dDrawMaterialBinding material,out T receipt,out IReadOnlyList<EnbW3dRenderVertexMaterial>? loadedMaterials,out IReadOnlyList<EnbCurrentRetailTextureObjectState>? loadedTextures,out Entry? retainedEntry,long revision=0)
        {
            if(_passes.TryGetValue(source,out var entries)&&entries.TryGetValue(key,out var entry)&&entry.Revision==revision)
            {
                Touch(entry);retainedEntry=entry;material=entry.Material;receipt=entry.Receipt;
                loadedMaterials=entry.LoadedMaterials;loadedTextures=entry.LoadedTextures;return true;
            }
            retainedEntry=null;material=default!;receipt=default!;loadedMaterials=null;loadedTextures=null;Misses++;return false;
        }
        public Entry Store(EnbW3dRenderMaterialPass source,Key key,EnbW3dDrawMaterialBinding material,T receipt,long revision=0,IReadOnlyList<EnbW3dRenderVertexMaterial>? loadedMaterials=null,IReadOnlyList<EnbCurrentRetailTextureObjectState>? loadedTextures=null)
        {
            if(!_passes.TryGetValue(source,out var entries))_passes.Add(source,entries=[]);
            if(entries.TryGetValue(key,out var previous))Remove(previous);
            _passes[source]=entries;
            var owner=(key.Identity,key.Generation);
            var entry=new Entry(source,key,material,receipt,_frame,revision,loadedMaterials,loadedTextures){Persistent=_persistent.Contains(owner)};
            entries[key]=entry;
            if(!_owners.TryGetValue(owner,out var owned))_owners.Add(owner,owned=[]);
            owned.Add(entry);if(!entry.Persistent)_temporary.Add(entry);
            return entry;
        }
        public void Touch(Entry entry){if(!entry.Persistent){entry.Frame=_frame;MarkSeenWrites++;}Hits++;}
        public void EndFrame(IReadOnlySet<(ulong Identity,ulong Generation)>? residentOwners)
        {
            LastSweepCandidateCount=0;_retire.Clear();
            foreach(var entry in _temporary)
            {
                LastSweepCandidateCount++;
                if(entry.Frame!=_frame&&residentOwners?.Contains((entry.Key.Identity,entry.Key.Generation))!=true)_retire.Add(entry);
            }
            foreach(var entry in _retire)Remove(entry);_retire.Clear();
        }
        public sealed class Entry(EnbW3dRenderMaterialPass source,Key key,EnbW3dDrawMaterialBinding material,T receipt,long frame,long revision,IReadOnlyList<EnbW3dRenderVertexMaterial>? loadedMaterials,IReadOnlyList<EnbCurrentRetailTextureObjectState>? loadedTextures)
        {
            public bool Active=true,Persistent;
            public readonly EnbW3dRenderMaterialPass Source=source;
            public readonly Key Key=key;
            public EnbW3dMeshDescriptor? PreparedInput,PreparedOutput;
            public readonly long Revision=revision;
            public readonly EnbW3dDrawMaterialBinding Material=material;
            public readonly T Receipt=receipt;
            public readonly IReadOnlyList<EnbW3dRenderVertexMaterial>? LoadedMaterials=loadedMaterials;
            public readonly IReadOnlyList<EnbCurrentRetailTextureObjectState>? LoadedTextures=loadedTextures;
            public long Frame=frame;
        }
    }
}
