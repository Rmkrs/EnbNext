using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSphereTransformTests
{
    [Fact]
    public void CameraFacingAxesPreservePlacementAndDiscardObjectBasis()
    {
        Assert.True(EnbRenderCameraBasis.TryCreate(Quaternion.CreateFromAxisAngle(Vector3.UnitZ,.7f),out var camera));
        var position=new Vector3(10,20,30);
        var world=Matrix4x4.CreateScale(8)*Matrix4x4.CreateRotationY(.4f)*Matrix4x4.CreateTranslation(position);
        var matrix=EnbRetailSphereTransform.Build(world,new(2,3,4),new(5,6,7),true,camera);
        Assert.Equal(position,matrix.Translation);
        Near(-camera.Forward*10,Vector3.TransformNormal(Vector3.UnitX,matrix));
        Near(camera.Right*18,Vector3.TransformNormal(Vector3.UnitY,matrix));
        Near(camera.Up*28,Vector3.TransformNormal(Vector3.UnitZ,matrix));
        Assert.Equal(matrix,EnbRetailSphereTransform.Build(Matrix4x4.CreateTranslation(position),new(2,3,4),new(5,6,7),true,camera));
    }

    [Fact]
    public void OrdinaryPathRetainsObjectRotationAndScalesBeforeTranslation()
    {
        var world=Matrix4x4.CreateRotationZ(.4f)*Matrix4x4.CreateTranslation(10,20,30);
        var matrix=EnbRetailSphereTransform.Build(world,new(2,3,4),new(5,6,7),false,default);
        Near(Vector3.Transform(new Vector3(10,18,28),world),Vector3.Transform(Vector3.One,matrix));
        Assert.Equal(world.Translation,matrix.Translation);
    }

    private static void Near(Vector3 expected,Vector3 actual)=>Assert.InRange(Vector3.Distance(expected,actual),0,1e-5f);
}
