using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Owns one exclusively compiled immediate overlay draw. The caller
/// transfers the lane, task and run states; it must not queue another view using
/// these objects. Asset/context replacement retires this owner as a unit.</summary>
public sealed class EnbW3dOverlayDrawOwner
{
    public EnbW3dProgrammableTextureLanePlan Lane { get; }
    private readonly EnbW3dMaterialUniforms[] authoredMaterials;
    public EnbW3dOverlayDrawOwner(EnbW3dProgrammableTextureLanePlan lane)
    {
        Lane=lane;
        authoredMaterials=new EnbW3dMaterialUniforms[lane.RunStates.Length];
        for(int i=0;i<authoredMaterials.Length;i++)authoredMaterials[i]=lane.RunStates[i].LightingRun.Material;
        // Establish private frame storage once if the compiled task inherited a
        // shared frame. Keep it alive with this draw, outside temporary arenas.
        var task=lane.Submission.Packet;
        task.SetCurrentRetailStage0UvOffset(task.CurrentRetailStage0UvOffset);
        task.RetainAfterDrain();
    }
    public void Update(long time,Vector2? stage0Offset,Vector3? emission)
    {
        Lane.Submission.Packet.SetFrameTimeMilliseconds(time).SetCurrentRetailStage0UvOffset(stage0Offset);
        for(int i=0;i<authoredMaterials.Length;i++)
            Lane.RunStates[i].LightingRun.Material=emission is {} rgb
                ? authoredMaterials[i] with {Emissive=rgb}:authoredMaterials[i];
    }
}
