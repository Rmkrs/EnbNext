using System.Numerics;
using System.Runtime.CompilerServices;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>
/// Current-client spatial transform handoff to RenderObject. The spatial
/// provider supplies a Matrix3D, either directly or from its quaternion. Immediately
/// before RenderObject::Set_Transform, UpdateSpaceObjectRuntime @ 0x006F3C60
/// validates that matrix through IsTransformBasisOrthonormal @ 0x008C2FC0 and
/// repairs a non-rigid basis through RepairTransformBasisOrResetIdentity @
/// 0x008C3180.
/// </summary>
public static class EnbCurrentRetailSpatialTransformRuntime
{
    // A snapshot is immutable. Many bounds, attachment, collision and drawing
    // consumers request this exact handoff again within the same frame. Weak
    // keys retain neither old frames nor objects after their consumers release them.
    internal sealed record ResolvedTransform(bool Success,Matrix4x4 World,string? Diagnostic);
    private static readonly ConditionalWeakTable<EnbSceneTransformSnapshot,ResolvedTransform> Resolved=[];
    public static bool TryBuildRigidWorld(IEnbRenderTransform transform,
        out Matrix4x4 rigidWorld,out string? diagnostic)
    {
        if(transform is Owner owner)
        {rigidWorld=owner.world;diagnostic=owner.diagnostic;return owner.success;}
        if(transform is not EnbSceneTransformSnapshot snapshot)
            return transform.ProviderTransform is {} matrix?ValidateWorld(matrix,out rigidWorld,out diagnostic)
                :TryBuildRigidWorld(transform.Position,transform.Orientation,out rigidWorld,out diagnostic);
        var result=Resolved.GetValue(snapshot,static value=>
        {
            Matrix4x4 world;string? error;
            bool success=value.ProviderTransform is {} provider
                ?ValidateWorld(provider,out world,out error)
                :TryBuildRigidWorld(value.Position,value.Orientation,out world,out error);
            return new(success,world,error);
        });
        rigidWorld=result.World;diagnostic=result.Diagnostic;return result.Success;
    }

    /// <summary>Mutable render-node transform. Consumers read this owner directly;
    /// diagnostic copies are explicit and detached.</summary>
    public sealed class Owner : IEnbRenderTransform
    {
        public uint ClientTime=>previous.Sample.ClientTime;
        public Vector3 Position=>previous.Sample.Position;
        public Quaternion Orientation=>previous.Sample.Orientation;
        public Vector3 Velocity=>previous.Sample.Velocity;
        public float Speed=>previous.Sample.Speed;
        public Vector3 Rotation=>previous.Sample.Rotation;
        public Vector3 OrientationColumn0=>previous.Sample.OrientationColumn0;
        public Matrix4x4? ProviderTransform=>previous.Sample.ProviderTransform;
        public float BankHeadingTarget=>previous.Sample.BankHeadingTarget;
        public EnbSceneTransformSnapshot CaptureDiagnostics()=>previous.ToTransform();
        internal EnbRenderPose previous;
        internal Matrix4x4 world;
        internal string? diagnostic;
        internal bool success;
        private bool resolved;
        public long TransformUpdates {get;private set;}
        private static bool Bits<T>(T left,T right) where T:unmanaged
            =>System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref left,1))
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref right,1)));
        private static bool SameProvider(EnbRenderPose a,EnbRenderPose b)
            =>a.Sample.ProviderTransform is {} left
                ?b.Sample.ProviderTransform is {} right&&Bits(left,right)
                :b.Sample.ProviderTransform is null;
        private static bool SameMatrixInputs(EnbRenderPose a,EnbRenderPose b)
            =>SameProvider(a,b)&&(a.Sample.ProviderTransform is not null||
                Bits(a.Sample.Position,b.Sample.Position)&&Bits(a.Sample.Orientation,b.Sample.Orientation));
        public Owner Update(EnbRenderPose pose)
        {
            if(!pose.HasSample)throw new ArgumentException("A sampled pose is required.",nameof(pose));
            if(!resolved||!SameMatrixInputs(previous,pose))
            {
                Matrix4x4 world;string? diagnostic;
                bool success=pose.Sample.ProviderTransform is {} provider
                    ?ValidateWorld(provider,out world,out diagnostic)
                    :TryBuildRigidWorld(pose.Sample.Position,pose.Sample.Orientation,out world,out diagnostic);
                this.success=success;this.world=world;this.diagnostic=diagnostic;resolved=true;TransformUpdates++;
            }
            previous=pose;
            return this;
        }
    }

    public static bool TryBuildRigidWorld(
        Vector3 position,
        Quaternion orientation,
        out Matrix4x4 rigidWorld,
        out string? diagnostic)
    {
        rigidWorld = default;
        diagnostic = null;
        if (!IsFinite(position) || !IsFinite(orientation))
        {
            diagnostic = "Spatial transform contains non-finite position/orientation data.";
            return false;
        }

        rigidWorld = EnbRetailCameraQuaternionBasis.Create(orientation);
        rigidWorld.Translation=position;
        return ValidateWorld(rigidWorld,out rigidWorld,out diagnostic);
    }

    private static bool ValidateWorld(Matrix4x4 provider,out Matrix4x4 rigidWorld,out string? diagnostic)
    {
        rigidWorld=provider;
        diagnostic=null;
        if (!IsFinite(rigidWorld))
        {
            diagnostic = "Spatial transform produced a non-finite matrix.";
            return false;
        }

        rigidWorld = EnbRetailSpatialBasis.Validate(rigidWorld).Transform;
        if (!IsFinite(rigidWorld))
        {
            diagnostic = "Current-retail spatial basis repair produced a non-finite matrix.";
            return false;
        }

        return true;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
