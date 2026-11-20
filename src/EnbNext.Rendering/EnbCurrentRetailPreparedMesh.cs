using System.Collections.Immutable;

namespace EnbNext.Rendering;

/// <summary>Persistent descriptor ranges. A draw is a value snapshot combining
/// these ranges with current instance inputs; it never edits an earlier draw.</summary>
internal sealed class EnbCurrentRetailPreparedModelLifetime
{
    internal bool Active = true;
    internal ulong MaterialRevision;
}

public sealed class EnbCurrentRetailPreparedMeshPass
{
    internal EnbCurrentRetailPreparedMeshPass(EnbW3dDrawTask packet,
        EnbCurrentRetailMeshContainerRegistry.Container container,
        IReadOnlyList<EnbCurrentRetailMeshSubmissionBatch> batches, uint materialRevision,
        (EnbW3dRenderTextureBinding Binding, object? Identity)[] textures)
    {
        Mesh = packet.Mesh; Material = packet.Material.Source;
        Mappers = packet.CurrentRetailMaterialMappers; SharedMappers = packet.CurrentRetailSharedMaterialMappers;
        Container = container; MaterialRevision = materialRevision; Textures = textures;
        // The model must not retain the first instance's frame/lighting/context
        // snapshot. Only static indexed ranges belong to registered geometry.
        Batches = batches.Select(batch => new PreparedBatch(batch.Identity,
            batch.Submission.VertexSource, batch.Submission.PrimitiveTopology,
            batch.Submission.Runs, batch.Submission.NPatchGeometry)).ToArray();
    }
    internal readonly EnbW3dRenderMesh Mesh;
    internal readonly EnbW3dRenderMaterialPass Material;
    internal readonly object? Mappers, SharedMappers;
    internal readonly uint MaterialRevision;
    internal readonly (EnbW3dRenderTextureBinding Binding, object? Identity)[] Textures;
    private readonly record struct PreparedBatch(object Identity, EnbW3dSubmissionVertexSource VertexSource,
        EnbW3dIndexedPrimitiveTopology Topology, ImmutableArray<EnbW3dIndexedSubmissionRun> Runs,
        EnbW3dNPatchGeometry? NPatchGeometry);
    private readonly PreparedBatch[] Batches;
    private EnbCurrentRetailPreparedModelLifetime? _lifetime;
    private ulong _materialRevision;
    internal bool IsCurrent => _lifetime is { Active: true } lifetime && lifetime.MaterialRevision == _materialRevision;
    internal void BindLifetime(EnbCurrentRetailPreparedModelLifetime lifetime)
    { _lifetime = lifetime; _materialRevision = lifetime.MaterialRevision; }
    public EnbCurrentRetailMeshContainerRegistry.Container Container { get; }
    public int Count => Batches.Length;
    public object BatchIdentity(int index) => Batches[index].Identity;
    public EnbW3dIndexedSubmissionPlan Submit(int index, EnbW3dDrawTask packet)
    {
        var batch = Batches[index];
        return new(packet, batch.VertexSource, batch.Topology, batch.Runs) { NPatchGeometry = batch.NPatchGeometry };
    }
}

public sealed partial class EnbCurrentRetailMeshBatchState
{
    private sealed class PreparedModel(EnbCurrentRetailMeshContainerRegistry.Container container)
    {
        public readonly EnbCurrentRetailPreparedModelLifetime Lifetime = new();
        public bool Active => Lifetime.Active;
        public readonly EnbCurrentRetailMeshContainerRegistry.Container Container = container;
        private readonly HashSet<EnbCurrentRetailMeshModelOwner.MaterialLocalWord> _materialWords = [];
        public void ObserveMaterial(EnbCurrentRetailMeshModelOwner.MaterialLocalWord word)
        { if (_materialWords.Add(word)) word.Changed += MaterialChanged; }
        private void MaterialChanged() => Lifetime.MaterialRevision++;
        public void Invalidate()
        {
            Lifetime.Active = false;
            foreach (var word in _materialWords) word.Changed -= MaterialChanged;
            _materialWords.Clear();
            Passes.Clear();
        }
        public readonly System.Runtime.CompilerServices.ConditionalWeakTable<EnbW3dRenderMaterialPass, EnbCurrentRetailPreparedMeshPass> Passes = new();
    }
    private sealed record ModelRegistration(object Context, PreparedModel Model);
    private sealed record PassRegistration(PreparedModel Model, EnbCurrentRetailPreparedMeshPass Pass);
    private readonly object _preparationContext = new();
    public Action<EnbW3dDrawTask, string>? PreparedPassLookupObserved { get; set; }
    public long PreparedModelLookupCount { get; private set; }
    public long PreparedPassLookupCount { get; private set; }
    public long RigidPreparedPassLookupCount { get; private set; }
    private readonly Dictionary<object, PreparedModel> _prepared = new(ReferenceEqualityComparer.Instance);
    private (bool Enabled, int Segments)? _preparedCapability;
    public long PreparedPassBuildCount { get; private set; }
    public long PreparedPassReuseCount { get; private set; }
    public int PreparedModelCount => _prepared.Count;

    /// <summary>00958750/0097AC10 register the model; 0093F390 subsequently
    /// queues its descriptors. Only a changed pass/binding or invalidated model
    /// recompiles topology. Pose/light/clock and visibility are not invalidation.</summary>
    public bool TryPrepareRigidPass(EnbW3dDrawTask packet, EnbCurrentRetailMeshAppearance appearance,
        bool nPatchEnabled, int nPatchSegments, Func<EnbW3dRenderTextureBinding, object?> resolveTexture,
        out EnbCurrentRetailPreparedMeshPass prepared, out string? diagnostic)
    {
        prepared = null!; diagnostic = null;
        if (packet.DeformedGeometry is not null || packet.CurrentRetailViewPositions is not null ||
            packet.Mesh.BoneIndices.Count != 0 || !packet.Mesh.CurrentRetailUsesMeshRenderer)
        { diagnostic = "Retained rigid preparation requires ordinary immutable mesh geometry."; return false; }
        return TryPreparePass(packet,appearance,nPatchEnabled,nPatchSegments,resolveTexture,out prepared,out diagnostic);
    }

    // 0097AE50 retains the skin model's categories and index ranges. Only
    // visible deformed vertices/base offsets belong to 009797A0's flush.
    public bool TryPrepareSkinPass(EnbW3dDrawTask packet, EnbCurrentRetailMeshAppearance appearance,
        bool nPatchEnabled, int nPatchSegments, Func<EnbW3dRenderTextureBinding, object?> resolveTexture,
        out EnbCurrentRetailPreparedMeshPass prepared, out string? diagnostic)
    {
        prepared=null!;diagnostic=null;
        if(packet.Mesh.BoneIndices.Count==0 || !packet.Mesh.CurrentRetailUsesMeshRenderer ||
            packet.DeformedGeometry is not {} skin || packet.CurrentRetailViewPositions is not null ||
            packet.WorldTransform!=System.Numerics.Matrix4x4.Identity ||
            skin.Positions.Count!=packet.Mesh.Positions.Count || skin.Normals.Count!=packet.Mesh.Normals.Count ||
            EnbW3dNPatchCompiler.IsEligible(packet,nPatchEnabled,nPatchSegments))
        {diagnostic="Retained skin descriptors require a complete deformed stream and unchanged authored topology.";return false;}
        return TryPreparePass(packet,appearance,nPatchEnabled,nPatchSegments,resolveTexture,out prepared,out diagnostic);
    }

    private bool TryPreparePass(EnbW3dDrawTask packet, EnbCurrentRetailMeshAppearance appearance,
        bool nPatchEnabled, int nPatchSegments, Func<EnbW3dRenderTextureBinding, object?> resolveTexture,
        out EnbCurrentRetailPreparedMeshPass prepared, out string? diagnostic)
    {
        prepared=null!;diagnostic=null;
        var model = PrepareModel(appearance, nPatchEnabled, nPatchSegments);
        if (packet.PreparedState.MeshPreparation is PassRegistration direct &&
            ReferenceEquals(direct.Model, model) && direct.Pass.IsCurrent &&
            direct.Pass.MaterialRevision == appearance.Owner.MaterialIdentityRevision &&
            TexturesMatch(direct.Pass, resolveTexture))
        {
            prepared = direct.Pass; PreparedPassReuseCount++; return true;
        }
        object identity = appearance.Owner.ModelIdentity;
        if (PreparedPassLookupObserved is {} observe)
        {
            var previous = packet.PreparedState.MeshPreparation as PassRegistration;
            observe(packet, previous is null ? "definition-unbound" : !ReferenceEquals(previous.Model, model) ? "model-changed" :
                !previous.Pass.IsCurrent ? "pass-invalidated" : previous.Pass.MaterialRevision != appearance.Owner.MaterialIdentityRevision ? "material-revision" : "external-texture");
        }
        PreparedPassLookupCount++;
        if (packet.Mesh.BoneIndices.Count == 0) RigidPreparedPassLookupCount++;
        if (model.Passes.TryGetValue(packet.Material.Source, out var prior) &&
            ReferenceEquals(prior.Mesh, packet.Mesh) && ReferenceEquals(prior.Material, packet.Material.Source) &&
            ReferenceEquals(prior.Mappers, packet.CurrentRetailMaterialMappers) &&
            ReferenceEquals(prior.SharedMappers, packet.CurrentRetailSharedMaterialMappers) &&
            prior.MaterialRevision == appearance.Owner.MaterialIdentityRevision && prior.IsCurrent && TexturesMatch(prior, resolveTexture))
        {
            packet.PreparedState.MeshPreparation = new PassRegistration(model, prior);
            prepared = prior; PreparedPassReuseCount++; return true;
        }
        if (!EnbW3dIndexedSubmissionCompiler.TryCompile(packet, out var indexed, out diagnostic) ||
            !EnbCurrentRetailMeshSubmissionBatches.TryCompile(indexed, identity, model.Container,
                Descriptors, appearance.Mappers, resolveTexture, out var batches, out diagnostic,
                EnbW3dNPatchCompiler.IsEligible(packet, nPatchEnabled, nPatchSegments))) return false;
        var textures = new List<(EnbW3dRenderTextureBinding, object?)>();
        foreach (var run in indexed.Runs)
            foreach (var binding in run.Binding.Textures)
                if (binding is { IsUnbound: false, CurrentRetailTextureState: null })
                    textures.Add((binding, resolveTexture(binding)));
        prepared = new(packet, model.Container, batches, appearance.Owner.MaterialIdentityRevision, textures.ToArray());
        foreach (var run in indexed.Runs)
            if (run.Binding.VertexMaterial is {} material)
                model.ObserveMaterial(EnbCurrentRetailMeshModelOwner.RetainMaterialRuntimeWord64(material.Material));
        prepared.BindLifetime(model.Lifetime);
        packet.PreparedState.MeshPreparation = new PassRegistration(model, prepared);
        model.Passes.Remove(packet.Material.Source);
        model.Passes.Add(packet.Material.Source, prepared); PreparedPassBuildCount++;
        return true;
    }
    public EnbCurrentRetailMeshContainerRegistry.Container PrepareRigidContainer(
        EnbCurrentRetailMeshAppearance appearance, bool nPatchEnabled, int nPatchSegments)
        => PrepareModel(appearance, nPatchEnabled, nPatchSegments).Container;

    public EnbCurrentRetailMeshContainerRegistry.Container PrepareSkinContainer(
        EnbCurrentRetailMeshAppearance appearance, bool nPatchEnabled, int nPatchSegments)
        => PrepareModel(appearance,nPatchEnabled,nPatchSegments).Container;

    private PreparedModel PrepareModel(EnbCurrentRetailMeshAppearance appearance, bool nPatchEnabled, int nPatchSegments)
    {
        var capability = (nPatchEnabled, nPatchSegments);
        if (_preparedCapability.HasValue && _preparedCapability.Value != capability) ResetBuffers();
        _preparedCapability = capability;
        if (appearance.Owner.PreparedRegistration is ModelRegistration registration &&
            ReferenceEquals(registration.Context, _preparationContext) && registration.Model.Active)
            return registration.Model;
        object identity = appearance.Owner.ModelIdentity;
        PreparedModelLookupCount++;
        if (!_prepared.TryGetValue(identity, out var model))
        {
            var mesh = appearance.Mesh;
            bool skin=mesh.BoneIndices.Count!=0;
            uint fvf = EnbCurrentRetailMeshBufferLayout.CalculateFvf(mesh.RetailTextureCoordinateSets.Count,
                mesh.EffectiveColor1.Count != 0, appearance.Passes.Any(p => p.SpecularColors.Count != 0),
                EnbCurrentRetailMeshBufferLayout.RequiresAuthoredNormals(mesh.Attributes, appearance.Passes, true,
                    nPatchEnabled, (uint)nPatchSegments), skin, mesh.GeometrySort.Requested,
                EnbW3dGeometrySortSemantics.CurrentRetailLowLevelSortingRendererEnabled);
            model = new(Containers.Register(identity, fvf, skin, false, (uint)mesh.Positions.Count,
                checked((uint)(EnbW3dNPatchCompiler.PreparedIndexCount(mesh, nPatchEnabled, nPatchSegments) * appearance.Passes.Length))));
            _prepared.Add(identity, model);
        }
        appearance.Owner.PreparedRegistration = new ModelRegistration(_preparationContext, model);
        return model;
    }
    private static bool TexturesMatch(EnbCurrentRetailPreparedMeshPass pass,
        Func<EnbW3dRenderTextureBinding, object?> resolve)
    {
        // Only unresolved/externally supplied texture identities need polling:
        // native TextureClass identity survives resource content/device updates.
        foreach (var texture in pass.Textures)
            if (!ReferenceEquals(texture.Identity, resolve(texture.Binding))) return false;
        return true;
    }
}
