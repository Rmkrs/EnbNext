using System.Numerics;
using EnbNext.Net7.World;
namespace EnbNext.Presentation;
/// <summary>Numeric transform storage owned by a presentation provider.</summary>
public sealed class EnbRenderTransformState : IEnbRenderTransform
{
    public Net7SpatialSample Value;
    public EnbRenderTransformState Set(Net7SpatialSample value){Value=value;return this;}
    public uint ClientTime=>Value.ClientTime;
    public Vector3 Position=>Value.Position;
    public Quaternion Orientation=>Value.Orientation;
    public Vector3 Velocity=>Value.Velocity;
    public float Speed=>Value.Speed;
    public Vector3 Rotation=>Value.Rotation;
    public Vector3 OrientationColumn0=>Value.OrientationColumn0;
    public Matrix4x4? ProviderTransform=>Value.ProviderTransform;
    public float BankHeadingTarget=>Value.BankHeadingTarget;
}
