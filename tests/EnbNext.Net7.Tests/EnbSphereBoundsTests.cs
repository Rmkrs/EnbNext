using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSphereBoundsTests
{
    [Fact]
    public void WorldUsesTranslatedAuthoredCenterAndAnimatedExtents()
    {
        var transform=Matrix4x4.CreateScale(9)*Matrix4x4.CreateRotationZ(MathF.PI/2)*Matrix4x4.CreateTranslation(10,20,30);
        var bounds=EnbRetailSphereBounds.World(new(1,2,3),new(3,4,0),new(2,2,1),transform);
        Assert.Equal(new Vector3(11,22,33),bounds.Center);
        Assert.Equal(10,bounds.Radius);
    }

    [Fact]
    public void LocalAndWorldHaveDistinctScaleSemantics()
    {
        var local=EnbRetailSphereBounds.Local(new(1,2,3),new(3,4,0));
        var world=EnbRetailSphereBounds.World(new(1,2,3),new(3,4,0),new(-2,0,1),Matrix4x4.Identity);
        Assert.Equal(local.Center,world.Center);
        Assert.Equal(5,local.Radius);
        Assert.Equal(6,world.Radius);
        Assert.Equal(0,EnbRetailSphereBounds.World(Vector3.Zero,Vector3.One,Vector3.Zero,Matrix4x4.Identity).Radius);
    }
}
