using System.Numerics;
using EnbNext.Rendering;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbGalaxyMapCameraTests
{
    [Theory]
    [InlineData(400,200,.5f,.25f)]
    [InlineData(200,400,.25f,.5f)]
    public void AuthoredViewportControlsProjectionAndSphereFit(float width,float height,float x,float y)
    {
        var camera=new EnbGalaxyMapCamera();
        var viewport=new Vector2(width,height);
        Assert.Equal(new Vector4(-x,-y,x,y),EnbGalaxyMapCamera.ProjectionBounds(viewport));
        camera.Frame(new(10,20,0),10,EnbGalaxyMapCamera.HomePitch,viewport,true);
        Assert.Equal(40,camera.Distance);
        var resolved=camera.Resolve(viewport,new(800,600,1));
        Assert.Equal(new EnbRenderClipRange(.01f,1000),resolved.Projection.ClipRange);
        Assert.True(resolved.TryProjectWorldPoint(camera.Center,0,out var center));
        Assert.InRange(center.PixelX,399.999,400.001);
        Assert.InRange(center.PixelY,299.999,300.001);
        Assert.InRange(Vector3.Distance(camera.Center,camera.SurfacePoint(new(.5f),viewport)),0,.00001f);
        Assert.InRange(resolved.Position.Y,6.319f,6.32f);
    }

    [Fact]
    public void TransitionConsumesRetainedSpeedCurveAndCompletesAtTarget()
    {
        var camera=new EnbGalaxyMapCamera();
        camera.Frame(Vector3.Zero,10,0,new(400,200),true);
        camera.Frame(new(100,0,0),20,EnbGalaxyMapCamera.ClickPitch,new(400,200),false);
        camera.Advance(100);
        Assert.InRange(camera.Transition,.019999f,.020001f);
        Assert.InRange(camera.Center.X,1.9999f,2.0001f);
        Assert.InRange(camera.Distance,40.7999f,40.8001f);
        camera.Advance(100);
        // t=.02 -> table index 0, fraction .2 -> speed .32 -> next t=.052.
        Assert.InRange(camera.Transition,.051999f,.052001f);
        for(int i=0;i<1000 && camera.Transition>=0;i++) camera.Advance(16);
        Assert.Equal(-1,camera.Transition);
        Assert.Equal(new Vector3(100,0,0),camera.Center);
        Assert.Equal(80,camera.Distance);
        Assert.InRange(camera.Basis.M23,.707106f,.707108f);
    }

    [Fact]
    public void ManualZoomCancelsTransitionAndUsesDistanceDependentRateAndBounds()
    {
        var camera=new EnbGalaxyMapCamera();
        camera.Frame(Vector3.Zero,10,0,new(400,200),true);
        camera.Frame(Vector3.One,10,0,new(400,200),false);
        camera.Zoom(.1f,100);
        Assert.Equal(-1,camera.Transition);
        Assert.InRange(camera.Distance,35.899f,35.901f);
        camera.Zoom(100,100); Assert.Equal(2,camera.Distance);
        camera.Zoom(-1000,100); Assert.Equal(300,camera.Distance);
    }

    [Fact]
    public void PanUsesCameraAxesAndRotationPreservesPivot()
    {
        var camera=new EnbGalaxyMapCamera();
        camera.Frame(new(10,20,0),10,0,new(400,200),true);
        camera.Pan(new(.1f,.2f),new(-100),new(100));
        Assert.Equal(new Vector3(14,12,0),camera.Center);
        camera.Rotate(new(0,.2f));
        Assert.Equal(new Vector3(14,12,0),camera.Center);
        Assert.InRange(MathF.Atan2(camera.Basis.M23,camera.Basis.M22),.1745f,.1746f);
        camera.Rotate(new(0,100));
        Assert.InRange(MathF.Atan2(camera.Basis.M23,camera.Basis.M22),0,1.000001f);
    }
}
