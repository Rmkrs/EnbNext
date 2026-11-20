using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSpiralSeekTests
{
    [Fact]
    public void AuthoredSpiralHasZeroEndpointOffsetAndFullRadiusAtMidpoint()
    {
        var source=new Vector3(10,20,30);var target=source+new Vector3(100,0,0);
        var start=EnbRetailSpiralSeekEffectPath.Sample(1000,0,30,1,source,target);
        var middle=EnbRetailSpiralSeekEffectPath.Sample(1000,500,30,1,source,target);
        var end=EnbRetailSpiralSeekEffectPath.Sample(1000,1000,30,1,source,target);
        Assert.True(Vector3.Distance(source,start.Translation)<.001f);
        Assert.True(Vector3.Distance(source+new Vector3(50,0,-30),middle.Translation)<.001f);
        Assert.True(Vector3.Distance(target,end.Translation)<.001f);
        var expected=Vector3.Normalize(new Vector3(100,-60*MathF.PI,0));
        Assert.True(Vector3.Distance(expected,new(middle.M11,middle.M12,middle.M13))<.001f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void OrientationFollowsTrajectoryAndRepeatedSamplesAreIdentical(int yaw)
    {
        var rotation=Matrix4x4.CreateRotationZ(yaw*MathF.PI/180);
        var source=new Vector3(20,-40,5);
        var target=source+Vector3.TransformNormal(new(600,0,0),rotation);
        for(uint age=200;age<1500;age+=113)
        {
            var pose=EnbRetailSpiralSeekEffectPath.Sample(1577,age,30,.25f,source,target);
            Assert.Equal(pose,EnbRetailSpiralSeekEffectPath.Sample(1577,age,30,.25f,source,target));
            var before=EnbRetailSpiralSeekEffectPath.Sample(1577,age-1,30,.25f,source,target);
            var after=EnbRetailSpiralSeekEffectPath.Sample(1577,age+1,30,.25f,source,target);
            Assert.True(Vector3.Distance(Vector3.Normalize(after.Translation-before.Translation),new(pose.M11,pose.M12,pose.M13))<.001f);
        }
    }

    [Fact]
    public void ZeroRadiusIsStraightAndZeroSpinStillHasAuthoredLateralArc()
    {
        var straight=EnbRetailSpiralSeekEffectPath.Sample(1000,500,0,10,Vector3.Zero,new(100,0,0));
        Assert.Equal(new Vector3(50,0,0),straight.Translation);
        var arc=EnbRetailSpiralSeekEffectPath.Sample(1000,500,30,0,Vector3.Zero,new(100,0,0));
        Assert.Equal(new Vector3(50,0,30),arc.Translation);
    }
}
