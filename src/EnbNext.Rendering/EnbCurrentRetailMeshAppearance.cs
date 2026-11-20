namespace EnbNext.Rendering;

/// <summary>The effective descriptor and retained model used by a mesh
/// invocation. Cameras borrow these owners; drawing is not a model copy.</summary>
public sealed record EnbCurrentRetailMeshAppearance(
    EnbW3dRenderMesh Mesh,
    EnbW3dRenderMaterialPass[] Passes,
    EnbCurrentRetailMeshModelOwner Owner,
    IReadOnlyDictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers> Mappers,
    bool PrototypeOwned);

/// <summary>One MeshClass::Render call, independent of its descriptor's
/// pass count. Packet copies retain this identity through later appearance
/// and camera preparation.</summary>
public sealed class EnbCurrentRetailMeshInvocation(
    EnbW3dDrawTask geometry, EnbCurrentRetailMeshAppearance appearance)
{
    // A direct slot in a synchronous visible-task scope; the token does not
    // reference its storage, so a queued invocation cannot retain a frame pool.
    internal object? TaskStorageToken;
    internal ulong TaskStorageEpoch;
    internal int TaskStorageSlot;
    public EnbW3dDrawTask Geometry { get; private set; } = geometry;
    public static EnbCurrentRetailMeshInvocation Create(EnbW3dDrawTask geometry,EnbCurrentRetailMeshAppearance appearance)
        => EnbW3dDrawTaskArena.Current?.RentInvocation(geometry,appearance) ?? new(geometry,appearance);
    internal void Reuse(EnbW3dDrawTask geometry,EnbCurrentRetailMeshAppearance appearance)
    { Geometry=geometry;Appearance=appearance;TaskStorageToken=null;TaskStorageEpoch=0;TaskStorageSlot=0; }
    internal void ClearAfterDrain()
    {Geometry=null!;Appearance=null!;TaskStorageToken=null;TaskStorageEpoch=0;TaskStorageSlot=0;}
    public EnbCurrentRetailMeshAppearance Appearance { get; private set; } = appearance;
}
