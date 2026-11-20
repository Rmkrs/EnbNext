using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10ScannerCameraTests
{
    [Theory]
    [InlineData(0.5f)]
    [InlineData(1f)]
    [InlineData(2f)]
    public void FitUsesModelScaleWithoutScalingWorldCenterTwice(float scale)
    {
        Vector3 center = new(32000, -4000, 500);
        Assert.True(EnbCurrentRetailScannerCameraRuntime.TryResolveFitSphere(
            new(center, 71258.84f), scale, out var sphere));
        Assert.Equal(center, sphere.Center);
        Assert.Equal(71258.84f * scale, sphere.Radius);
    }

    [Fact]
    public void AlreadyScaledShipLegoBoundKeepsItsRadius()
    {
        var compiled = new EnbW3dWorldBounds(new(100, 200, 300), 17);
        Assert.True(EnbCurrentRetailScannerCameraRuntime.TryResolveFitSphere(compiled, 1, out var sphere));
        Assert.Equal(compiled, sphere);
    }

    [Theory]
    [InlineData(-1000, 0, 0)]
    [InlineData(0, -1000, 0)]
    [InlineData(300, 200, 100)]
    [InlineData(0, 0, 1000)]
    [InlineData(0, 0, 0)]
    public void LiveObserverControlsRollZeroDirectionAndSphereFit(float x, float y, float z)
    {
        var observer = new Vector3(x,y,z);
        Assert.True(EnbCurrentRetailScannerCameraRuntime.TryCreate(observer, Vector3.Zero,
            Vector3.Zero,100,new(400,300,1),1,out var camera));
        Vector3 forward = observer == Vector3.Zero ? Vector3.UnitX : Vector3.Normalize(-observer);
        Assert.InRange(Vector3.Distance(forward,camera.Basis.Forward),0,0.00001f);
        Assert.InRange(Vector3.Distance(-forward*180,camera.Position),0,0.0001f);
        Assert.InRange(MathF.Abs(camera.Basis.Right.Z),0,0.00001f);
        Assert.False(camera.Projection.IsOrthographic);
        Assert.Equal(1,camera.Projection.ClipRange.NearPlane);
        Assert.Equal(380,camera.Projection.ClipRange.FarPlane,3);
        Assert.True(camera.TryProjectWorldPoint(Vector3.Zero,0,out var center));
        Assert.Equal(200,center.PixelX,3);
        Assert.Equal(150,center.PixelY,3);
    }

    [Fact]
    public void AuthoredAspectIsIndependentOfStretchedPanelPixels()
    {
        Assert.True(EnbCurrentRetailScannerCameraRuntime.TryCreate(new(-1000,0,0),Vector3.Zero,
            Vector3.Zero,100,new(800,300,1),0.75,out var camera));
        Assert.Equal(2/0.75,camera.Projection.Matrix.M11,5);
        Assert.Equal(2,camera.Projection.Matrix.M22,5);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.8f)]
    [InlineData(2.4f)]
    public void SelfTargetOffCenterBoundTracksHeadingWithoutCancellingBank(float yaw)
    {
        // Self observer and target share a root position. A bound centered aft
        // of that root makes the ordinary look-at fit follow the ship's heading.
        // This is not a ship-specific nose camera and must not be used for peers.
        var viewport = new EnbRenderViewport(400,300,1);
        var heading = Quaternion.CreateFromAxisAngle(Vector3.UnitZ,yaw);
        Vector3 root = new(1000,2000,0);
        Vector3 center = root + Vector3.Transform(new Vector3(-30,0,0),heading);
        Assert.True(EnbCurrentRetailScannerCameraRuntime.TryCreate(root,root,center,100,viewport,1,out var self));
        Assert.InRange(Vector3.Distance(self.Basis.Forward,
            Vector3.Transform(-Vector3.UnitX,heading)),0,0.00001f);
        Assert.InRange(MathF.Abs(self.Basis.Right.Z),0,0.00001f);
        Vector3 wing = new(0,20,0);
        var bank = Quaternion.CreateFromAxisAngle(Vector3.UnitX,0.5f);
        Assert.True(self.TryProjectWorldPoint(center + Vector3.Transform(wing,heading),0,out var level));
        Assert.True(self.TryProjectWorldPoint(center + Vector3.Transform(Vector3.Transform(wing,bank),heading),0,out var tilted));
        Assert.True(Math.Abs(tilted.PixelY-level.PixelY)>5);
        Assert.True(EnbCurrentRetailScannerCameraRuntime.TryCreate(root+Vector3.Transform(new Vector3(0,500,0),heading),
            root,center,100,viewport,1,out var peer));
        Assert.True(Vector3.Distance(self.Basis.Forward,peer.Basis.Forward)>1);
    }

    [Fact]
    public void WorldTranslationAndScaleDoNotChangeNormalizedLandmarkPosition()
    {
        var viewport = new EnbRenderViewport(600,400,1);
        Vector3 observer = new(-1000,200,300), offset = new(32000,-4000,500);
        Vector3 landmark = new(5,20,10);
        Assert.True(EnbCurrentRetailScannerCameraRuntime.TryCreate(observer,Vector3.Zero,
            Vector3.Zero,100,viewport,1,out var a));
        Assert.True(EnbCurrentRetailScannerCameraRuntime.TryCreate(observer*3+offset,offset,
            offset,300,viewport,1,out var b));
        Assert.True(a.TryProjectWorldPoint(landmark,0,out var pa));
        Assert.True(b.TryProjectWorldPoint(landmark*3+offset,0,out var pb));
        Assert.InRange(Math.Abs(pa.PixelX-pb.PixelX),0,0.002);
        Assert.InRange(Math.Abs(pa.PixelY-pb.PixelY),0,0.002);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    public void InvalidBoundsAreRejected(float radius)
        => Assert.False(EnbCurrentRetailScannerCameraRuntime.TryCreate(Vector3.Zero,Vector3.Zero,
            Vector3.Zero,radius,new(300,300,1),1,out _));
}
