namespace EnbNext.Rendering;

/// <summary>Prepared skin material state and current synchronous draw inputs.
/// Current is borrowed until ReleaseFrame; queued tasks and diagnostic histories
/// retain their own inputs, never this mutable lane.</summary>
public sealed class EnbW3dSkinLaneOwner
{
    public EnbW3dProgrammableTextureLanePlan Definition { get; }
    public EnbW3dProgrammableTextureLanePlan Current { get; }
    public bool HasFrame { get; private set; }
    public EnbW3dSkinLaneOwner(EnbW3dProgrammableTextureLanePlan definition)
    {
        Definition=definition;
        Current=definition.Copy().SetLightingPlan(definition.LightingPlan.Copy());
    }
    public bool TryBind(EnbW3dIndexedSubmissionPlan submission,EnbResolvedRenderCamera camera,out string? diagnostic)
    {
        if(HasFrame)throw new InvalidOperationException("A skin lane must finish its current draw before rebinding.");
        diagnostic=null;
        var previous=Definition.Submission.Packet;var packet=submission.Packet;
        if(!ReferenceEquals(previous.Mesh,packet.Mesh) || !ReferenceEquals(previous.Material.Source,packet.Material.Source) ||
            previous.CurrentRetailColorizationRevision!=packet.CurrentRetailColorizationRevision ||
            previous.CurrentRetailNameDecalRevision!=packet.CurrentRetailNameDecalRevision ||
            previous.CurrentRetailDecalRevision!=packet.CurrentRetailDecalRevision ||
            !Definition.Submission.Runs.AsSpan().SequenceEqual(submission.Runs.AsSpan()))return false;
        if(!EnbW3dProgrammableLightingLaneCompiler.TryUpdateOwnedSkinFrame(Current.LightingPlan,submission,camera,out diagnostic))return false;
        Current.CanonicalCameraProjection=camera.Projection.Matrix;
        Current.RetailGeneratedCoordinateViewFromWorld=EnbW3dGeneratedTextureCoordinates.CreateRetailCameraViewFromWorld(camera);
        HasFrame=true;return true;
    }
    public void ReleaseFrame()
    {
        if(!HasFrame)return;
        Current.LightingPlan.Submission=Definition.Submission;
        Current.LightingPlan.Normals=Definition.LightingPlan.Normals;
        Current.LightingPlan.LightEnvironment=Definition.LightingPlan.LightEnvironment;
        HasFrame=false;
    }
}
