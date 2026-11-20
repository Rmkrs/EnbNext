namespace EnbNext.Rendering;

/// <summary>
/// Shared mesh renderer ownership: model retirement removes descriptor links;
/// container destruction invalidates remaining links. A native reset (0097B2D0)
/// releases buffers, not live RenderObj/model references.
/// </summary>
public sealed partial class EnbCurrentRetailMeshBatchState
{
    private ulong _resetRevision;
    public EnbCurrentRetailMeshContainerRegistry Containers { get; } = new();
    public EnbCurrentRetailDescriptorBatchRegistry Descriptors { get; } = new();

    public EnbCurrentRetailMeshModelOwner CreateModel(
        IEnumerable<(object Material, object? Mapper0, object? Mapper1)> materials,
        IEnumerable<object>? loadedTextures = null,EnbCurrentRetailMeshGeometryState? geometry=null)
        => new(materials, RetireModel, loadedTextures,geometry);

    public EnbCurrentRetailMeshModelOwner CreateModel(EnbW3dRenderResourceCache prototypes,
        EnbW3dRenderMesh mesh, IEnumerable<(object Material, object? Mapper0, object? Mapper1)> materials)
        => prototypes.CreateModelOwner(mesh, materials, RetireModel);

    private void RetireModel(object model)
    {
        if (_prepared.Remove(model, out var prepared)) prepared.Invalidate();
        Descriptors.RemoveModel(model);
        Containers.Remove(model);
    }

    public bool TryCreateModel(EnbW3dRenderResourceCache prototypes, EnbW3dRenderMesh mesh,
        IEnumerable<EnbW3dRenderVertexMaterial> loadedMaterials, uint constructionClock,
        out EnbCurrentRetailMeshModelOwner owner, out string? diagnostic,
        IReadOnlyList<EnbCurrentRetailTextureObjectState>? loadedTextures=null)
        => prototypes.TryCreateModelOwner(mesh, loadedMaterials, constructionClock,
            out owner, out diagnostic, RetireModel,loadedTextures:loadedTextures);

    /// <summary>
    /// Call only after a proven ship-material mutation gate. 0080EAF0,
    /// 0080F470, 00810130 and 00812390 force 00940300 when model+88's
    /// descriptor-link list is nonempty, even for a sole model owner.
    /// </summary>
    public void PrepareRetainedMaterialMutation(EnbCurrentRetailMeshModelOwner owner, uint creationMilliseconds)
        => owner.EnsureUniqueModel(creationMilliseconds, Descriptors.HasModelLinks(owner.ModelIdentity));

    public void ObserveResetRevision(ulong revision)
    {
        if (_resetRevision == revision) return;
        _resetRevision = revision;
        ResetBuffers();
    }

    public void ResetBuffers()
    {
        foreach (var prepared in _prepared.Values) prepared.Invalidate();
        _prepared.Clear();
        Descriptors.Clear();
        Containers.Clear();
    }

    public void EndFrame()
    {
        Containers.EndFrame(RetireContainer);
    }

    private void RetireContainer(EnbCurrentRetailMeshContainerRegistry.Container container)
    {
        foreach (var model in container.Models)
            if (_prepared.Remove(model, out var prepared)) prepared.Invalidate();
        Descriptors.RemoveContainer(container);
    }
}
