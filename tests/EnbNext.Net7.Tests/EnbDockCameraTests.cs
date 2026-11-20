using System.Numerics;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;

public sealed class EnbDockCameraTests
{
    [Fact]
    public void DepartureReversesAuthoredPathAndUsesExitLead()
    {
        var definition=EnbRetailDockCameraDefinition.Resolve(EnbRetailIniDocument.Parse(
            "[station]\nBone=HANGER\nSX=-2000\nEX=10\nSpeed=0.4\nDuration=5\nEnterLead=100\nExitLead=700"),"station")!;
        var runtime=new EnbDockCameraRuntime(new EnbRetailCameraRandomSource(() => 0u));
        var ship=EnbCameraControlTests.Ship(0,0);
        var anchor=new EnbDockCameraAnchor(99,definition,Matrix4x4.Identity);
        var pose=runtime.Update(new(1,99,0,true),ship,anchor,0,out bool complete)!.Value;
        Assert.False(complete);
        Assert.Equal(new Vector3(10,0,0),runtime.ShipTransform!.Position);
        Assert.Equal(-690,pose.Position!.Value.X);
        runtime.Update(new(1,99,0,true),ship,anchor,1000,out complete);
        Assert.Equal(new Vector3(-390,0,0),runtime.ShipTransform!.Position);
        Assert.False(complete);
        runtime.Update(new(1,99,0,true),ship,anchor,5001,out complete);
        Assert.True(complete);
    }
    [Fact]
    public void AuthoredBoneFrameControlsPathCameraSpeedAndStrictCompletionTime()
    {
        var definition=EnbRetailDockCameraDefinition.Resolve(EnbRetailIniDocument.Parse(
            "[station]\nBone=HANGER\nSX=-2000\nEX=10\nSpeed=0.4\nDuration=5\nAngle=70"),"STATION")!;
        var bone=Matrix4x4.CreateRotationZ(MathF.PI/2)*Matrix4x4.CreateTranslation(100,200,300);
        var runtime=new EnbDockCameraRuntime(new EnbRetailCameraRandomSource(() => 0u));
        var ship=EnbCameraControlTests.Ship(0,0);
        var request=new Net7DockCameraTransition(1,99,100);
        var anchor=new EnbDockCameraAnchor(99,definition,bone);
        var first=runtime.Update(request,ship,anchor,100,out bool complete)!.Value;
        Assert.False(complete);Assert.True(first.Snap);Assert.True(first.ForceThirdPerson);
        Assert.True(Vector3.Distance(new(100,-1790,300),runtime.ShipTransform!.Position)<.001f);
        Assert.Equal(Vector3.Zero,ship.Spatial!.Transform.Position);
        var second=runtime.Update(request,ship,anchor,1100,out complete)!.Value;
        Assert.Equal(first.Position,second.Position);
        Assert.True(Vector3.Distance(new(100,-1390,300),runtime.ShipTransform!.Position)<.001f);
        runtime.Update(request,ship,anchor,5100,out complete);Assert.False(complete);
        runtime.Update(request,ship,anchor,5101,out complete);Assert.True(complete);
        runtime.Update(request,ship,anchor,5102,out complete);Assert.False(complete);
        Assert.Null(runtime.Update(null,ship,anchor,5103,out _));Assert.Null(runtime.ShipTransform);
    }
    [Fact]
    public void MissingPathCompletesOnceAndBinaryDefaultDurationIsFourSeconds()
    {
        var ini=EnbRetailIniDocument.Parse("[station]\nBone=HANGER\n[missing]\nSpeed=1");
        Assert.Equal(4000,EnbRetailDockCameraDefinition.Resolve(ini,"station")!.DurationMilliseconds);
        Assert.Null(EnbRetailDockCameraDefinition.Resolve(ini,"missing"));
        var runtime=new EnbDockCameraRuntime();var ship=EnbCameraControlTests.Ship(0,0);
        Assert.Null(runtime.Update(new(1,99,0),ship,null,0,out bool complete));Assert.True(complete);
        Assert.Null(runtime.Update(new(1,99,0),ship,null,1,out complete));Assert.False(complete);
    }
}
