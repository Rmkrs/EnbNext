namespace EnbNext.Rendering;

/// <summary>Authored occurrence metadata prepared with the mesh, shared by its
/// passes and frame tasks. It is not a frame publication or a physical lifetime.</summary>
public sealed class EnbW3dMeshOccurrenceIdentity(int repetition)
{
    public int Repetition { get; } = repetition;
    // The physical receiver owns marks. This is its direct binding in this
    // prepared occurrence; the application checks physical owner on rebinding.
    public object? ProjectedDecalReceiver { get; set; }
}
