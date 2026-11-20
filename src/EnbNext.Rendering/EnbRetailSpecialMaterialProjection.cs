using System.Numerics;

namespace EnbNext.Rendering;

// A126D0's source matrix is retained by the RenderObj; the resulting matrix
// is stored on the shared member wrapper. A missing target does not update it.
public sealed record EnbRetailSpecialMaterialProjection(Matrix4x4 NativeSource, int Width, int Height, int FormatBits)
{
    public Matrix4x4 Create(Matrix4x4 world, EnbResolvedRenderCamera camera)
    {
        EnbRenderCameraMatrixCompiler.TryCreateWorldToCanonicalView(camera,out var view);
        float depth=uint.MaxValue>>((32-FormatBits)&31);
        var bias=new Matrix4x4(.5f,0,0,0,0,-.5f,0,0,0,0,depth,0,.5f+.5f/Width,.5f+.5f/Height,0,1);
        // 8DE8F0(a,b,out) is row-major a*b. A126D0 forms bias*source,
        // world*cameraInverse, then multiplies those two results. 945BF0
        // returns the inverse camera transform at +354 (944EB0 ->8C2BC0).
        // The wrapper uploads its raw rows as c30..33. Transpose once into
        // the System.Numerics convention consumed by the register compiler.
        return Matrix4x4.Transpose(bias*NativeSource*Matrix4x4.Transpose(world)*Matrix4x4.Transpose(view));
    }
}

// A118D0 leaves the wrapper matrix uninitialized until A126D0 succeeds.
// Shared member identity, rather than a device or clone, owns this state.
public sealed class EnbRetailSpecialMaterialProjectionState : EnbNext.Assets.EnbRenderPrepared
{
    // The resource's render-prepared slot holds this state.
    public static EnbRetailSpecialMaterialProjectionState For(EnbNext.Assets.EnbCurrentRetailSpecialMaterialResource resource)
        =>(EnbRetailSpecialMaterialProjectionState)OwnerCache.Get(ref resource.RenderPrepared,resource,
            static _=>(EnbNext.Assets.EnbRenderPrepared)new EnbRetailSpecialMaterialProjectionState());
    public Matrix4x4? Matrix {get;set;}
}
