using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbProjectedPassParentTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(900f)]
    public void NativeCount2CompositionUsesProjectionRowsWithoutDepthDivision(float depth)
    {
        // Independent numeric oracle: owner projector gives U=40,V=12.
        // Parent consumes (40,12,1,1), mapped to canonical (40,12,-1,1).
        // Camera projection gives X=86.5,Y=48.5,W=2.5; COUNT2 retains XY.
        var projector = new Matrix4x4(2,-1,8,0, 3,4,9,0, 5,2,10,0, 7,-6,11,1);
        var projection = new Matrix4x4(2,.25f,0,0, .5f,3,0,0, .75f,-.5f,depth,-2, 1.25f,2,3,.5f);
        var mapper = new EnbW3dMapperRuntimeEvaluation(new(-2,3),Vector2.Zero,new(.125f,-.25f),default,false);
        var composed = EnbCurrentRetailPassEffectDefinition.ComposeProjectedLinearParent(projector,projection,mapper);
        var actual = Vector4.Transform(new Vector4(2,3,4,1),composed);
        Assert.Equal(-172.6875f,actual.X,4);
        Assert.Equal(144.875f,actual.Y,4);
        Assert.Equal(2.5f,actual.W,4);
    }

    [Fact]
    public void CameraProjectionChangeContributesToPassCoordinates()
    {
        var definition = new EnbCurrentRetailPassEffectDefinition("fixture",new(-1,-1),false);
        var projector = definition.ProjectionFromWorld(Matrix4x4.Identity,new(10,20,30),256);
        var p = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI/2,4f/3,1,1000);
        var q = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI/3,4f/3,1,1000);
        var a = Vector4.Transform(new Vector4(2,3,4,1),EnbCurrentRetailPassEffectDefinition.ComposeProjectedLinearParent(projector,p,EnbW3dMapperRuntimeEvaluation.Identity));
        var b = Vector4.Transform(new Vector4(2,3,4,1),EnbCurrentRetailPassEffectDefinition.ComposeProjectedLinearParent(projector,q,EnbW3dMapperRuntimeEvaluation.Identity));
        Assert.Equal(a.X*MathF.Sqrt(3),b.X,4);
        Assert.Equal(a.Y*MathF.Sqrt(3),b.Y,4);
    }
}
