using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbCombatSeekTests
{
    [Theory]
    [InlineData(0,100,20)]
    [InlineData(0,10,10)]
    [InlineData(10,100,20)]
    [InlineData(10,-100,-20)]
    [InlineData(-100,-30,-30)]
    [InlineData(-100,-20,-20)]
    [InlineData(30,100,100)]
    public void TargetSphereChoosesAForwardHitWithinTheSegment(float start,float end,float expected)
    {
        var hit=EnbRetailSeekEffectPath.IntersectTarget(new(start,0,0),new(end,0,0),Vector3.Zero,20);
        Assert.InRange(MathF.Abs(hit.X-expected),0,.0001f);
        Assert.Equal(0,hit.Y);Assert.Equal(0,hit.Z);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(2784)]
    [InlineData(2303)]
    public void MissileEndpointsFollowTargetWithoutMovingLaunchPoint(uint duration)
    {
        var path=new EnbRetailSeekEffectPath(duration,1000,100,false,7);
        var start=new Vector3(120,-70,30);var target=new Vector3(-20,250,100);
        Assert.True(Vector3.Distance(start,path.Sample(0,start,target).Translation)<.001f);
        Assert.True(Vector3.Distance(target,path.Sample(duration,start,target).Translation)<.001f);
        target+=new Vector3(40,-20,5);
        Assert.True(Vector3.Distance(target,path.Sample(duration,start,target).Translation)<.001f);
        var middle=path.Sample(duration/2,start,target);
        Assert.True(float.IsFinite(middle.M11));
        Assert.InRange(new Vector3(middle.M11,middle.M12,middle.M13).Length(),.999f,1.001f);
    }

    [Fact]
    public void ImpactUsesTargetSurfaceAndPointsOutward()
    {
        var start=new Vector3(-100,0,0);var centre=Vector3.Zero;
        var hit=EnbRetailSeekEffectPath.IntersectTarget(start,centre,centre,20);
        // Native reciprocal-square-root normalization is not exact division.
        Assert.InRange(Vector3.Distance(new Vector3(-20,0,0),hit),0,.0001f);
        var pose=EnbRetailSeekEffectPath.BetweenPoints(centre,hit);
        Assert.Equal(centre,pose.Translation);Assert.Equal(-1,pose.M11);
        Assert.Equal(Matrix4x4.Identity,EnbRetailSeekEffectPath.BetweenPoints(centre,centre));
        Assert.Equal(new Vector3(-100,100,0),EnbRetailSeekEffectPath.IntersectTarget(start,new(-100,100,0),centre,20));
    }
}
