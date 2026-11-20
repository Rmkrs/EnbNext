namespace EnbNext.Rendering;

/// <summary>One owner-qualified query occurrence's retained base geometry.
/// Transform, visibility and traversal order belong to the published query,
/// never this slot. 00940530/0092AE10 share model topology with rendering.</summary>
public sealed class EnbCameraCollisionGeometrySlot
{
    private EnbW3dRenderMesh? _mesh;
    private object? _positions;
    private float _scale;
    private uint _mask;
    private ulong _vectorRevision;
    private EnbCurrentRetailMeshAabTree? _tree;
    private EnbCameraCollisionGeometry? _geometry;

    public EnbCameraCollisionGeometry Get(EnbW3dRenderMesh mesh, float scale, uint mask)
    {
        var positions = mesh.CurrentRetailLoadedGeometry?.Positions ?? mesh.Positions;
        // Vector identity plus explicit mutation revision retains scaled query
        // snapshots too. A pose change does not rebuild model/query topology.
        if ( ReferenceEquals(_mesh, mesh) && ReferenceEquals(_positions, positions) &&
            _scale == scale && _mask == mask && _geometry is not null &&
            _vectorRevision==(mesh.CurrentRetailLoadedGeometry?.VectorRevision??0) &&
            ReferenceEquals(_tree,mesh.CurrentRetailLoadedGeometry?.Tree)) return _geometry;
        var geometry = new EnbCameraCollisionGeometry(mesh, scale, mask);
        _mesh = mesh;
        _positions = positions;
        _vectorRevision=mesh.CurrentRetailLoadedGeometry?.VectorRevision??0;
        _tree=mesh.CurrentRetailLoadedGeometry?.Tree;
        _scale = scale; _mask = mask; _geometry = geometry;
        return geometry;
    }
}
