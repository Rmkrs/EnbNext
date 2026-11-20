using EnbNext.Net7.World;
namespace EnbNext.Presentation;

/// <summary>Detached numeric frame input, addressed by the slot assigned in the
/// ordered change stream. It carries no structural keys or mutable world objects.</summary>
public readonly record struct EnbRenderPose(bool HasSample,Net7SpatialSample Sample)
{
    public bool HasWarpInputs {get;init;}
    public int EngineWarpState {get;init;}
    public int CachedWarpState {get;init;}
    public System.Collections.Immutable.ImmutableArray<EnbSceneWarpPresentationChange> WarpChanges {get;init;}
    public bool HasPrivateWarpInputs {get;init;}
    public System.Collections.Immutable.ImmutableArray<EnbSceneWarpPresentationChange> PrivateWarpChanges {get;init;}
    public bool HasVisualInputs {get;init;}
    public float? ShipVisualBankRadians {get;init;}
    public EnbSceneShipEmotePlayback? ShipEmote {get;init;}
    public static EnbRenderPose FromObject(IEnbRenderObject value)
        =>FromTransform(value.Spatial?.Transform) with {HasVisualInputs=true,
            ShipVisualBankRadians=value.ShipVisualBankRadians,ShipEmote=value.ShipEmote};
    public static EnbRenderPose FromTransform(IEnbRenderTransform? transform)
        =>transform is null?default:new(true,new(transform.ClientTime,transform.Position,transform.Orientation,
            transform.Velocity,transform.Speed,transform.Rotation){OrientationColumn0=transform.OrientationColumn0,
            ProviderTransform=transform.ProviderTransform,BankHeadingTarget=transform.BankHeadingTarget});
    public EnbSceneTransformSnapshot ToTransform(object? rendererState=null)=>new(Sample.ClientTime,Sample.Position,Sample.Orientation,
        Sample.Velocity,Sample.Speed,Sample.Rotation,Sample.OrientationColumn0)
        {ProviderTransform=Sample.ProviderTransform,BankHeadingTarget=Sample.BankHeadingTarget,RendererTransformState=rendererState};
}
