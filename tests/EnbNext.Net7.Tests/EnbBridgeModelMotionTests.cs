using System.Numerics;
using EnbNext.Rendering;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbBridgeModelMotionTests
{
    [Fact]
    public void RateSmoothingUsesPresentationUpdatesAndRecoversOutsideMainBridgeState()
    {
        var motion = new EnbBridgeModelMotion();
        Assert.Equal(EnbRetailBridgeResource.ModelRoot,motion.Update(Vector3.Zero,0));
        motion.Update(new(.01f,.02f,.03f),0);
        Assert.True(Vector3.Distance(new(.1f,.2f,.3f),motion.SmoothedAngularRate)<1e-6);
        motion.Update(new(.01f,.02f,.03f),0);
        Assert.True(Vector3.Distance(new(.18f,.36f,.54f),motion.SmoothedAngularRate)<1e-6);
        motion.Update(new(10,20,30),1);
        Assert.True(Vector3.Distance(new(.144f,.288f,.432f),motion.SmoothedAngularRate)<1e-6);
        motion.Reset(); Assert.Equal(Vector3.Zero,motion.SmoothedAngularRate);
    }

    [Fact]
    public void ModelRotationMatchesRetailColumnOperationsAndDoesNotTranslate()
    {
        var motion = new EnbBridgeModelMotion();
        var result = motion.Update(new(0,.02f,.03f),0);
        Matrix4x4.Invert(EnbRetailBridgeResource.ModelRoot,out var inverseRoot);
        var reaction = inverseRoot * result;
        // Rx(.2), then Ry(-.3), then Rz(.3), applied to the three basis columns.
        var x = new Vector3(MathF.Cos(.3f)*MathF.Cos(.3f),MathF.Cos(.3f)*MathF.Sin(.3f),MathF.Sin(.3f));
        Assert.True(Vector3.Distance(x,Vector3.TransformNormal(Vector3.UnitX,reaction))<1e-6);
        Assert.Equal(Vector3.Zero,result.Translation);
        Assert.InRange(Math.Abs(result.GetDeterminant()-1),0,1e-6);
    }
}
