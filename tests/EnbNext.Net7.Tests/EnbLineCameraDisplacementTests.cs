using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbLineCameraDisplacementTests
{
    private static EnbResolvedRenderCamera Camera()
    {
        Assert.True(EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,
            new EnbRenderViewport(800,600,1),Math.PI/2,new(1,1000),out var camera));
        return camera with {Projection=camera.Projection with {StoredProjectionBounds=new(-1,-.75f,1,.75f)}};
    }
    [Theory]
    [InlineData(true,false,-1.375f,.6875f)]
    [InlineData(false,true,-2.5f,1.25f)]
    [InlineData(true,true,-1.375f,.6875f)]
    public void MovesOnlyTheFlaggedEndpointAndPrefersSource(bool source,bool target,float y,float z)
    {
        var start=new Vector3(0,-.25f,.125f);var end=new Vector3(3,-.25f,.125f);
        EnbRetailLineCameraDisplacement.Apply(Camera(),0,0,source,target,ref start,ref end);
        Assert.Equal(new Vector3(source?0:3,y,z),source?start:end);
        Assert.Equal(new Vector3(source?3:0,-.25f,.125f),source?end:start);
    }
    [Theory]
    [InlineData(false,false,0,-.25f,3)]
    [InlineData(true,false,2,-.25f,3)]
    [InlineData(false,true,0,-2,3)]
    public void NonlocalNoncrossingAndOutsideLinesAreUnchanged(bool source,bool target,float depth,float lateral,float endDepth)
    {
        var start=new Vector3(depth,lateral,.125f);var end=new Vector3(endDepth,lateral,.125f);
        var a=start;var b=end;
        EnbRetailLineCameraDisplacement.Apply(Camera(),.1f,.1f,source,target,ref start,ref end);
        Assert.Equal(a,start);Assert.Equal(b,end);
    }
    [Fact]
    public void FullWidthAndNoiseExpandTheNearPlaneBoundary()
    {
        var camera=Camera();var start=new Vector3(0,-.25f,0);var end=new Vector3(3,-.25f,0);
        EnbRetailLineCameraDisplacement.Apply(camera,.2f,.1f,true,false,ref start,ref end);
        //Intersection at depth one moves from screen X .25 to 1 + width + noise.
        Assert.InRange(-Vector3.Lerp(start,end,1f/3).Y,1.29999f,1.30001f);
    }
    [Fact]
    public void CameraTranslationAndRotationMapTheDisplacementBackToWorld()
    {
        var camera=Camera();var rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitZ,MathF.PI/2);
        var position=new Vector3(32,-16,8);
        Assert.True(EnbRenderCameraBasis.TryCreate(rotation,out var basis));
        camera=camera with {WorldOrientation=rotation,Basis=basis,Position=position};
        var start=Vector3.Transform(new Vector3(0,-.25f,.125f),rotation)+position;
        var end=Vector3.Transform(new Vector3(3,-.25f,.125f),rotation)+position;
        var originalEnd=end;
        EnbRetailLineCameraDisplacement.Apply(camera,0,0,true,false,ref start,ref end);
        Assert.InRange(Vector3.Distance(start,Vector3.Transform(new Vector3(0,-1.375f,.6875f),rotation)+position),0,.0001f);
        Assert.Equal(originalEnd,end);
    }
    [Fact]
    public void ExactCenterSingularityIsContainedWithoutChoosingAnArbitraryDirection()
    {
        var start=Vector3.Zero;var end=new Vector3(3,0,0);
        Assert.Throws<InvalidDataException>(()=>EnbRetailLineCameraDisplacement.Apply(Camera(),.1f,0,true,false,ref start,ref end));
        Assert.Equal(Vector3.Zero,start);Assert.Equal(new Vector3(3,0,0),end);
    }
}
