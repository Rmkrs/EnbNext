using EnbNext.Presentation;
namespace EnbNext.Rendering;
public sealed class EnbRenderSpatialState : IEnbRenderSpatial
{
    public EnbSceneSpatialProviderKind ProviderKind {get;private set;}
    public EnbCurrentRetailSpatialTransformRuntime.Owner Pose {get;}=new();
    public IEnbRenderTransform Transform=>Pose;
    public EnbScenePlanetSnapshot? Planet {get;private set;}
    public EnbSceneAdvancedSpatialSnapshot? Advanced {get;private set;}
    public uint? FormationLeaderId {get;private set;}
    public string ModelAudit {get;private set;}="";
    public uint? PacketTime {get;private set;}
    public uint? ReceiptTime {get;private set;}
    public uint TransitionMilliseconds {get;private set;}
    public void AdoptDefinition(IEnbRenderSpatial definition)
    {
        ProviderKind=definition.ProviderKind;Planet=definition.Planet;Advanced=definition.Advanced;
        FormationLeaderId=definition.FormationLeaderId;ModelAudit=definition.ModelAudit;
        PacketTime=definition.PacketTime;ReceiptTime=definition.ReceiptTime;TransitionMilliseconds=definition.TransitionMilliseconds;
        Pose.Update(EnbRenderPose.FromTransform(definition.Transform));
    }
    // The RenderObj owns this matrix; view invocations never mutate the source provider.
    public void SetWorldTransform(IEnbRenderTransform source,System.Numerics.Matrix4x4 world)
    {
        var sample=EnbRenderPose.FromTransform(source).Sample with
        {
            Position=world.Translation,Orientation=System.Numerics.Quaternion.CreateFromRotationMatrix(world),
            ProviderTransform=world,OrientationColumn0=new(world.M11,world.M12,world.M13)
        };
        Pose.Update(new(true,sample));
    }
}
