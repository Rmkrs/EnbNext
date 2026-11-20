using System.Numerics;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;

public sealed class EnbLandCameraTests
{
    [Fact]
    public void SurfaceArrivalConsumesPhysicalMatrixRatherThanQuaternionMetadata()
    {
        var pattern=EnbRetailLandingPattern.Resolve(EnbRetailIniDocument.Parse(
            "[Default]\nSX=-4000\nSY=120\nSZ=2000\nSpeedForward=1000\nSpeedDown=500"),1);
        var ship=EnbCameraControlTests.Ship(0,0);
        var physical=Matrix4x4.CreateRotationZ(.8f);physical.Translation=new(300,400,500);
        ship=ship with {Spatial=ship.Spatial! with {Transform=ship.Spatial.Transform with
            {ProviderTransform=physical,Orientation=Quaternion.Identity,Position=physical.Translation}}};
        var expected=EnbRetailLandingConstruction.Evaluate(physical,pattern.StartOffset,pattern.SpeedForward,pattern.SpeedDown);
        var runtime=new EnbLandCameraRuntime(new EnbRetailCameraRandomSource(()=>0x80000000u));
        var request=new Net7LandCameraTransition(1,99,0,Net7LandCameraPhase.SurfaceArrival);
        runtime.Update(request,ship,null,0,pattern,_=>0,0,true,out _);
        Assert.Equal(expected.Start,runtime.ShipTransform!.Position);
        Assert.NotEqual(physical.Translation+pattern.StartOffset,runtime.ShipTransform.Position);
        Assert.NotNull(runtime.ShipTransform.ProviderTransform);
        runtime.Update(request,ship,null,0,pattern,_=>0,4000,true,out _);
        Assert.Equal(expected.End,runtime.ShipTransform!.Position);
        Assert.True(Vector3.Distance(new(physical.M11,physical.M12,physical.M13),runtime.ShipTransform.OrientationColumn0)<.0001f);
    }
    [Fact]
    public void SurfaceArrivalUsesAuthoredOffsetsAndReturnsToPhysicalShipAfterFourSeconds()
    {
        var pattern=EnbRetailLandingPattern.Resolve(EnbRetailIniDocument.Parse(
            "[Default]\nSX=-4000\nSY=0\nSZ=2000\nSpeedForward=1000\nSpeedDown=500"),1);
        var ship=EnbCameraControlTests.Ship(0,0);
        var runtime=new EnbLandCameraRuntime(new EnbRetailCameraRandomSource(() => 0x80000000u));
        var request=new Net7LandCameraTransition(1,99,100,Net7LandCameraPhase.SurfaceArrival);
        var first=runtime.Update(request,ship,null,0,pattern,_=>50,100,true,out bool complete)!.Value;
        Assert.False(complete);Assert.Equal(new Vector3(-4000,0,2000),runtime.ShipTransform!.Position);
        Assert.Equal(new Vector3(0,0,150),first.Position);
        var middle=runtime.Update(request,ship,null,0,pattern,_=>50,2100,true,out complete)!.Value;
        Assert.False(complete);Assert.Equal(first.Position,middle.Position);Assert.NotEqual(first.Orientation,middle.Orientation);
        runtime.Update(request,ship,null,0,pattern,_=>50,4100,true,out complete);
        Assert.False(complete);Assert.Equal(ship.Spatial!.Transform.Position,runtime.ShipTransform!.Position);
        Assert.True(Vector3.Distance(Vector3.UnitX,runtime.ShipTransform.OrientationColumn0)<.0001f);
        runtime.Update(request,ship,null,0,pattern,_=>50,4101,true,out complete);Assert.True(complete);
        runtime.Update(request,ship,null,0,pattern,_=>50,4102,true,out complete);Assert.False(complete);
        Assert.Null(runtime.Update(null,ship,null,0,pattern,null,4103,true,out _));Assert.Null(runtime.ShipTransform);
    }
    [Fact]
    public void SpaceApproachMovesShipImmediatelyWithIndependentRotatingCamera()
    {
        var ship=EnbCameraControlTests.Ship(0,0);
        var planet=ship with {GameId=99,RenderIdentity=99,IsLocalShip=false,
            Spatial=ship.Spatial! with {Transform=ship.Spatial.Transform with {Position=new(-10000,0,0)}}};
        var runtime=new EnbLandCameraRuntime(new EnbRetailCameraRandomSource(() => 0u));
        var request=new Net7LandCameraTransition(1,99,0,Net7LandCameraPhase.SpaceApproach);
        var first=runtime.Update(request,ship,planet,10000,null,null,0,true,out bool complete)!.Value;
        Assert.False(complete);Assert.Equal(new Vector3(-4900,0,0),runtime.ShipTransform!.Position);
        var second=runtime.Update(request,ship,planet,10000,null,null,1000,true,out complete)!.Value;
        Assert.Equal(first.Position,second.Position);Assert.NotEqual(first.Orientation,second.Orientation);
        Assert.NotEqual(new Vector3(-4900,0,0),runtime.ShipTransform.Position);
        Assert.Equal(Vector3.Zero,ship.Spatial.Transform.Position);
        var disabled=new EnbLandCameraRuntime();
        Assert.Null(disabled.Update(request,ship,planet,10000,null,null,0,false,out complete));Assert.True(complete);
    }
    [Fact]
    public void SectorPatternOverridesDefaultWithoutHardcodedInstalledValues()
    {
        var ini=EnbRetailIniDocument.Parse("[Default]\nSX=-4000\n[42]\nSX=-120\nSY=4\nSZ=60\nUX=1\nUZ=0\nSpeedForward=30\nSpeedDown=10");
        Assert.Equal(-4000,EnbRetailLandingPattern.Resolve(ini,1).StartOffset.X);
        var selected=EnbRetailLandingPattern.Resolve(ini,42);
        Assert.Equal(new Vector3(-120,4,60),selected.StartOffset);Assert.Equal(Vector3.UnitX,selected.InitialUp);
        Assert.Equal(30,selected.SpeedForward);Assert.Equal(10,selected.SpeedDown);
    }
}
