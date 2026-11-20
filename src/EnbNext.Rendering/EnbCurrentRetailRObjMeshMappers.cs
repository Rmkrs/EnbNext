namespace EnbNext.Rendering;

/// <summary>Retained mesh material owners for an RObj resource. Factory
/// localization00496AE0 copies shared mesh models before activation006EF200;
/// activation00939750 resets the loaded table, independently of GPU lifetime.</summary>
public sealed class EnbCurrentRetailRObjMeshMappers : IDisposable
{
    private readonly EnbCurrentRetailMeshModelOwner[] _models;
    private readonly EnbCurrentRetailMeshBatchState? _batches;
    public EnbW3dRenderResource Resource { get; private set; }
    public IReadOnlyDictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers> Mappers { get; }

    /// <summary>Retain acquired RenderObj model references across asynchronous
    /// preparation without localizing or resetting their shared mapper state.</summary>
    public EnbCurrentRetailRObjMeshMappers RetainRenderObjects(EnbW3dRenderResource prepared)
    {
        var indices=prepared.Meshes.Select(mesh=>Enumerable.Range(0,Resource.Meshes.Count)
            .FirstOrDefault(index=>ReferenceEquals(mesh,Resource.Meshes[index]),-1)).ToArray();
        if(indices.Any(index=>index<0))
            throw new ArgumentException("Prepared meshes must belong to the acquired model table.",nameof(prepared));
        return new(prepared,indices.Select(index=>_models[index].CloneRenderObject()).ToArray(),Mappers,_batches);
    }

    public EnbCurrentRetailRObjMeshMappers(EnbW3dRenderResource resource, uint constructionClock,
        EnbCurrentRetailMeshBatchState? batches = null,
        EnbW3dRenderResourceCache? prototypes = null, IReadOnlyList<EnbW3dRenderMesh?>? registeredMeshes = null,
        IReadOnlyDictionary<EnbW3dRenderMesh,EnbCurrentRetailRObjMeshMappers>? retainedModels = null)
        : this(resource,constructionClock,batches,prototypes,registeredMeshes,null,retainedModels) { }

    private EnbCurrentRetailRObjMeshMappers(EnbW3dRenderResource resource, uint constructionClock,
        EnbCurrentRetailMeshBatchState? batches, EnbW3dRenderResourceCache? prototypes,
        IReadOnlyList<EnbW3dRenderMesh?>? registeredMeshes,
        IReadOnlyDictionary<EnbW3dRenderVertexMaterial,EnbW3dSharedMaterialMappers>? inheritedMappers,
        IReadOnlyDictionary<EnbW3dRenderMesh,EnbCurrentRetailRObjMeshMappers>? retainedModels = null)
    {
        if ((prototypes is null) != (registeredMeshes is null) || registeredMeshes is not null && registeredMeshes.Count != resource.Meshes.Count)
            throw new ArgumentException("Registered model sources must match the prepared mesh table.");
        Resource = resource;
        _batches = batches;
        var retained = new Dictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers>(ReferenceEqualityComparer.Instance);
        var models = new List<EnbCurrentRetailMeshModelOwner>();
        try
        {
            foreach (var mesh in resource.Meshes)
            {
                var registered = registeredMeshes?[models.Count];
                EnbCurrentRetailRObjMeshMappers? configured=null;
                if(registered is not null)retainedModels?.TryGetValue(registered,out configured);
                var meshMappers=configured?.Mappers??inheritedMappers;
                var entries = new List<(object Material, object? Mapper0, object? Mapper1)>();
                foreach (var material in mesh.CurrentRetailLoadedMaterials)
                {
                    if (material is null) { entries.Add((null!, null, null)); continue; }
                    var pair = Retain(material, retained, constructionClock, prototypes, meshMappers);
                    entries.Add((material, material.Mapper0 is null ? null : pair.Stage0,
                        material.Mapper1 is null ? null : pair.Stage1));
                }
                if(configured is not null)
                {
                    //48B220 can acquire a previously generated prototype.
                    //Its model/material mapper owners are not FILE prototypes.
                    int index=Enumerable.Range(0,configured.Resource.Meshes.Count)
                        .Single(i=>ReferenceEquals(configured.Resource.Meshes[i],registered));
                    models.Add(configured._models[index].CloneRenderObject());
                }
                else if (registered is not null)
                {
                    EnbCurrentRetailMeshModelOwner owner;
                    string? error;
                    bool success = batches is null
                        ? prototypes!.TryCreateModelOwner(registered,mesh.CurrentRetailLoadedMaterials,constructionClock,out owner,out error)
                        : batches.TryCreateModel(prototypes!,registered,mesh.CurrentRetailLoadedMaterials,constructionClock,out owner,out error);
                    if (!success) throw new InvalidDataException(error);
                    models.Add(owner);
                }
                else models.Add(batches is null ? new(entries, loadedTextures: mesh.CurrentRetailLoadedTextureStates,
                        geometry:mesh.CurrentRetailLoadedGeometry)
                    : batches.CreateModel(entries, mesh.CurrentRetailLoadedTextureStates,mesh.CurrentRetailLoadedGeometry));
                // A descriptor pointer outside the loaded table is still a
                // retained material until an actual model-copy remaps it.
                foreach (var pass in mesh.MaterialPasses)
                    foreach (var binding in pass.VertexMaterials)
                        if (!binding.IsUnbound) Retain(binding.Material, retained, constructionClock, prototypes, meshMappers);
            }
            _models = models.ToArray();
            Mappers = retained;
        }
        catch { foreach (var model in models) model.Dispose(); throw; }
    }

    private EnbCurrentRetailRObjMeshMappers(EnbW3dRenderResource resource,
        EnbCurrentRetailMeshModelOwner[] models,
        IReadOnlyDictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers> mappers,
        EnbCurrentRetailMeshBatchState? batches)
        => (Resource, _models, Mappers, _batches) = (resource, models, mappers, batches);

    public EnbCurrentRetailMeshAppearance CreateAppearance(EnbW3dRenderMesh mesh,
        EnbW3dRenderMaterialPass[] passes)
    {
        for (int i = 0; i < Resource.Meshes.Count; i++)
            if (ReferenceEquals(Resource.Meshes[i], mesh))
                return new(mesh, passes, _models[i], Mappers, true);
        throw new ArgumentException("The mesh does not belong to this retained RObj owner.", nameof(mesh));
    }

    private static EnbW3dSharedMaterialMappers Retain(EnbW3dRenderVertexMaterial material,
        Dictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers> retained, uint clock,
        EnbW3dRenderResourceCache? prototypes = null,
        IReadOnlyDictionary<EnbW3dRenderVertexMaterial,EnbW3dSharedMaterialMappers>? inheritedMappers = null)
    {
        if (retained.TryGetValue(material, out var pair)) return pair;
        if(inheritedMappers is not null && inheritedMappers.TryGetValue(material,out pair))
        {
            retained.Add(material,pair);
            return pair;
        }
        if (prototypes is not null)
        {
            if (!prototypes.TryRetainMaterialMappers([material],clock,out var published,out var diagnostic))
                throw new InvalidDataException(diagnostic);
            pair=published[material];
        }
        else if (!EnbW3dSharedMaterialMappers.TryCreate(material, clock, out pair, out var error,
                EnbW3dRetailMapperRandomSource.Process)) throw new InvalidDataException(error);
        retained.Add(material, pair);
        return pair;
    }

    public EnbCurrentRetailRObjMeshMappers Localize(uint constructionClock, bool markMaterials = true, bool compositeOccurrences = false)
    {
        if (compositeOccurrences)
        {
            using var occurrences = RetainCompositeOccurrences();
            return occurrences.Localize(constructionClock, markMaterials);
        }
        var models = new List<EnbCurrentRetailMeshModelOwner>();
        var meshes = new List<EnbW3dRenderMesh>();
        var retained = new Dictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers>(ReferenceEqualityComparer.Instance);
        try
        {
            for (int i = 0; i < _models.Length; i++)
            {
                var source = _models[i];
                var copy = source.CloneRenderObject();
                models.Add(copy);
                copy.EnsureUniqueModel(constructionClock);
                if (markMaterials) copy.MarkLoadedMaterialsInstanceLocal();
                var mesh = Resource.Meshes[i];
                if (!EnbW3dRenderResourceCompiler.TryCopyModelDescriptor(mesh, source, copy, out var passes, out var error))
                    throw new InvalidDataException(error);
                var loaded = Enumerable.Range(0, copy.LoadedMaterialCount)
                    .Select(index => (EnbW3dRenderVertexMaterial)copy.MaterialIdentityAt(index)).ToArray();
                foreach (var material in loaded)
                    if (material is not null) Retain(material, retained, constructionClock);
                foreach (var pass in passes)
                    foreach (var binding in pass.VertexMaterials)
                        if (!binding.IsUnbound && !retained.ContainsKey(binding.Material))
                        {
                            // Empty loaded tables retain descriptor pointers.
                            retained.Add(binding.Material, Mappers[binding.Material]);
                        }
                meshes.Add((mesh).Copy().SetCurrentRetailLoadedGeometry(copy.Geometry).SetMaterialPasses(passes).SetCurrentRetailLoadedMaterials(loaded).SetCurrentRetailLoadedMaterialRuntime(mesh.CurrentRetailLoadedMaterialRuntime
                        .Select((runtime, index) => runtime with { Material = loaded[index] }).ToArray()));
            }
            return new(Resource.Copy().SetMeshes(meshes), models.ToArray(), retained, _batches);
        }
        catch { foreach (var model in models) model.Dispose(); throw; }
    }

    public void Activate(uint clock)
    {
        foreach (var model in _models) model.ResetLoadedMappers(clock);
    }

    //98E020/98E050 enumerate every level in order, then secondary children.
    //48BE60 prepares each concrete Mesh occurrence, even when several refer
    //to one registered model. Give the packet projection distinct internal
    //bindings while keeping the authored mesh name and all nonmesh members.
    private EnbCurrentRetailRObjMeshMappers RetainCompositeOccurrences()
    {
        var meshes = new List<EnbW3dRenderMesh>();
        var models = new List<EnbCurrentRetailMeshModelOwner>();
        EnbW3dRenderHlodMember Retain(EnbW3dRenderHlodMember member)
        {
            int index = -1;
            for (int i = 0; i < Resource.Meshes.Count; i++)
                if (EnbW3dRenderObjectNameSemantics.MatchesMesh(Resource.Meshes[i], member.Name))
                {
                    if (index != -1) throw new InvalidDataException($"Ambiguous configured mesh '{member.Name}'.");
                    index = i;
                }
            if (index == -1) return member;
            var mesh = (Resource.Meshes[index]).Copy().SetContainerName($"<configured:{meshes.Count}>");
            meshes.Add(mesh);
            models.Add(_models[index].CloneRenderObject());
            return member with { Name = EnbW3dRenderObjectNameSemantics.RegisteredMeshName(mesh) };
        }
        try
        {
            var hlods = Resource.HierarchicalLods.Count != 0 ? Resource.HierarchicalLods :
                Resource.HierarchyModels.Select(model => EnbW3dHmodelConversion.Create(model, _ => true)).ToArray();
            var retained = hlods.Select(hlod => hlod with
            {
                Levels = hlod.Levels.Select(level => level with { Members = level.Members.Select(Retain).ToArray() }).ToArray(),
                Aggregate = hlod.Aggregate is {} aggregate ? aggregate with { Members = aggregate.Members.Select(Retain).ToArray() } : null,
            }).ToArray();
            return new(Resource.Copy().SetMeshes(meshes).SetHierarchicalLods(retained).SetHierarchyModels([]),
                models.ToArray(), Mappers, _batches);
        }
        catch { foreach (var model in models) model.Dispose(); throw; }
    }

    /// <summary>0048B220/0048BCF0 copy the source model before applying HSV.
    /// Scale-only configuration still copies mapper objects, but only HSV
    /// marks materials local. A concrete direct mesh also prepares private
    /// scaled geometry and its factory tree; GPU vertices retain the packet's
    /// explicit CurrentRetailMeshModelScale contract. Composite preparation
    /// enumerates concrete mesh occurrences in native level/member order.</summary>
    public EnbCurrentRetailRObjMeshMappers Configure(uint clock,
        EnbNext.Assets.EnbRetailTextureResolver textures, System.Numerics.Vector3 hsv,
        float? geometryScale = null, bool compositeOccurrences = false)
    {
        if (!compositeOccurrences && geometryScale is not null && (Resource.Meshes.Count != 1 ||
            Resource.HierarchicalLods.Count != 0 || Resource.HierarchyModels.Count != 0))
            throw new ArgumentException("Direct mesh preparation requires one concrete mesh.", nameof(geometryScale));
        using var occurrences = compositeOccurrences ? RetainCompositeOccurrences() : null;
        return (occurrences ?? this).ConfigureModels(clock,textures,hsv,geometryScale);
    }

    /// <summary>48B220/48BE60 copies the complete HLOD tree before scaling,
    /// HSV adjustment and48E9D0. Independently attached children participate
    /// in that same sequence, rather than completing one factory per child.</summary>
    public static IReadOnlyList<EnbCurrentRetailRObjMeshMappers> ConfigureCompositeGroup(
        IReadOnlyList<EnbCurrentRetailRObjMeshMappers> sources,uint clock,
        EnbNext.Assets.EnbRetailTextureResolver textures,System.Numerics.Vector3 hsv,float scale)
    {
        if(sources.Count==0)return [];
        var occurrences=new List<EnbCurrentRetailRObjMeshMappers>();
        var result=new List<EnbCurrentRetailRObjMeshMappers>();
        try
        {
            foreach(var source in sources)occurrences.Add(source.RetainCompositeOccurrences());
            var meshes=occurrences.SelectMany(o=>o.Resource.Meshes).ToArray();
            var mappers=new Dictionary<EnbW3dRenderVertexMaterial,EnbW3dSharedMaterialMappers>(ReferenceEqualityComparer.Instance);
            foreach(var source in occurrences)
                foreach(var pair in source.Mappers)mappers.TryAdd(pair.Key,pair.Value);
            using var combined=new EnbCurrentRetailRObjMeshMappers(sources[0].Resource.Copy().SetMeshes(meshes).SetHierarchicalLods([]).SetHierarchyModels([]),
                occurrences.SelectMany(o=>o._models).Select(m=>m.CloneRenderObject()).ToArray(),mappers,sources[0]._batches);
            using var configured=combined.ConfigureModels(clock,textures,hsv,scale);
            int offset=0;
            foreach(var source in occurrences)
            {
                var resource=source.Resource.Copy().SetMeshes(configured.Resource.Meshes.Skip(offset).Take(source.Resource.Meshes.Count).ToArray());
                result.Add(configured.RetainRenderObjects(resource));
                offset+=source.Resource.Meshes.Count;
            }
            return result;
        }
        catch {foreach(var owner in result)owner.Dispose();throw;}
        finally {foreach(var owner in occurrences)owner.Dispose();}
    }

    /// <summary>48BE60/48D710 stop at Collection;991220 still forwards scale.
    /// Mesh93EDD0 then copies a shared model at that child's scale dispatch,
    /// rather than in the earlier HLOD preparation walk.</summary>
    public EnbCurrentRetailRObjMeshMappers ConfigureCollectionTree(uint clock,
        EnbNext.Assets.EnbRetailTextureResolver textures,System.Numerics.Vector3 hsv,float scale,
        IReadOnlyList<bool> outsideCollection)
    {
        if(outsideCollection.Count!=Resource.Meshes.Count)throw new ArgumentException("Mesh traversal flags must match the retained table.",nameof(outsideCollection));
        var children=new List<EnbCurrentRetailRObjMeshMappers>();
        try
        {
            foreach(var mesh in Resource.Meshes)children.Add(RetainRenderObjects(Resource.Copy().SetMeshes([mesh])));
            void LocalizeChild(int index,bool mark)
            {
                var copy=children[index].Localize(clock,mark);
                children[index].Dispose();children[index]=copy;
            }
            for(int i=0;i<children.Count;i++)
                if(outsideCollection[i])LocalizeChild(i,EnbCurrentRetailPrototypeVariantName.ChangesHsv(hsv));
            for(int i=0;i<children.Count;i++)
            {
                if(scale==1)continue;
                if(!outsideCollection[i])LocalizeChild(i,false);
                var child=children[i];var mesh=child.Resource.Meshes[0];
                var random=EnbRetailCrtRandomSource.Current;uint seed=random.State;
                try{mesh.CurrentRetailLoadedGeometry?.Scale(new(scale),ref seed);}
                finally{random.State=seed;}
                child.Resource=child.Resource.Copy().SetMeshes([(mesh).Copy().SetCurrentRetailLoadedGeometryScale(mesh.CurrentRetailLoadedGeometryScale*scale)]);
            }
            if(hsv!=System.Numerics.Vector3.Zero)
                for(int i=0;i<children.Count;i++)
                    if(outsideCollection[i])
                    {
                        var child=children[i];
                        var shifted=EnbCurrentRetailEffectMeshHsvRuntime.Apply(child.Resource,textures,hsv);
                        children[i]=new(shifted,clock,_batches,null,null,child.Mappers);
                        child.Dispose();
                    }
            var crt=EnbRetailCrtRandomSource.Current;uint state=crt.State;
            try
            {
                foreach(var child in children)
                    foreach(var mesh in child.Resource.Meshes)
                        mesh.CurrentRetailLoadedGeometry?.PostprocessFactoryTree(mesh.CurrentRetailRenderPassCount,ref state);
            }
            finally{crt.State=state;}
            var mappers=new Dictionary<EnbW3dRenderVertexMaterial,EnbW3dSharedMaterialMappers>(ReferenceEqualityComparer.Instance);
            foreach(var child in children)foreach(var pair in child.Mappers)mappers.TryAdd(pair.Key,pair.Value);
            return new(Resource.Copy().SetMeshes(children.SelectMany(c=>c.Resource.Meshes).ToArray()),
                children.SelectMany(c=>c._models).Select(m=>m.CloneRenderObject()).ToArray(),mappers,_batches);
        }
        finally{foreach(var child in children)child.Dispose();}
    }

    private EnbCurrentRetailRObjMeshMappers ConfigureModels(uint clock,
        EnbNext.Assets.EnbRetailTextureResolver textures,System.Numerics.Vector3 hsv,float? geometryScale)
    {
        var copy = Localize(clock, EnbCurrentRetailPrototypeVariantName.ChangesHsv(hsv));
        bool transferred=false;
        try
        {
            if (geometryScale is {} scale)
            {
                //48B220 -> Mesh +164/93EDD0 ->92D460, before HSV and48E9D0.
                //98EF60 applies that dispatch to every retained HLOD member.
                var random=EnbRetailCrtRandomSource.Current;uint seed=random.State;
                try
                {
                    foreach (var mesh in copy.Resource.Meshes)
                        if (scale != 1 && mesh.CurrentRetailLoadedGeometry is {} geometry)
                            geometry.Scale(new(scale), ref seed);
                }
                finally { random.State=seed; }
                //92D460 multiplies the already-retained geometry. A nested
                //generated-name request must retain the cumulative model scale.
                copy.Resource=copy.Resource.Copy().SetMeshes(copy.Resource.Meshes.Select(mesh => (mesh).Copy().SetCurrentRetailLoadedGeometryScale(mesh.CurrentRetailLoadedGeometryScale*scale)).ToArray());
            }
            var resource = hsv == System.Numerics.Vector3.Zero ? copy.Resource :
                EnbCurrentRetailEffectMeshHsvRuntime.Apply(copy.Resource, textures, hsv);
            if(geometryScale is not null)
            {
                var random=EnbRetailCrtRandomSource.Current;uint seed=random.State;
                try
                {
                    foreach (var mesh in resource.Meshes)
                        mesh.CurrentRetailLoadedGeometry?.PostprocessFactoryTree(mesh.CurrentRetailRenderPassCount, ref seed);
                }
                finally { random.State=seed; }
            }
            if(hsv==System.Numerics.Vector3.Zero)
            {
                transferred=true;
                return copy;
            }
            //0048D1F0 only mutates the loaded material table. Descriptor-only
            //materials surviving00940300's empty-table copy retain their
            //existing mapper objects, even without explicit mapper owners.
            return new(resource, clock, _batches, null, null, copy.Mappers);
        }
        finally { if(!transferred)copy.Dispose(); }
    }

    public void Dispose() { foreach (var model in _models) model.Dispose(); }
}
