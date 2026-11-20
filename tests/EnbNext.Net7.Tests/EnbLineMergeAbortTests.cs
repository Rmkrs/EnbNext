using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbLineMergeAbortTests
{
    [Fact]
    public void BothCentersMustFitPerpendicularDistanceAndEqualityIsAccepted()
    {
        var ray=Vector3.UnitX;
        var a=new Vector3(1000000,1,0);var b=new Vector3(-1000000,0,1);
        Assert.True(EnbRetailLineJoin.WithinMergeAbortRaw(ray,a,b,2,.5f));
        var outside=new Vector3(0,MathF.BitIncrement(1),0);
        Assert.False(EnbRetailLineJoin.WithinMergeAbortRaw(ray,outside,b,2,.5f));
        Assert.False(EnbRetailLineJoin.WithinMergeAbortRaw(ray,a,outside,2,.5f));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)]
    public void NonpositiveFactorDisablesDistanceRejection(float factor)
        =>Assert.True(EnbRetailLineJoin.WithinMergeAbortRaw(Vector3.UnitX,new(0,10000,0),new(0,0,10000),1,factor));

    [Fact]
    public void ZeroWidthAndDegenerateCandidateAreNotSilentlyNormalized()
    {
        Assert.True(EnbRetailLineJoin.WithinMergeAbortRaw(Vector3.UnitX,new(10,0,0),new(-10,0,0),0,1));
        Assert.False(EnbRetailLineJoin.WithinMergeAbortRaw(Vector3.UnitX,new(10,.01f,0),Vector3.Zero,0,1));
        Assert.True(EnbRetailLineJoin.WithinMergeAbortRaw(Vector3.Zero,Vector3.UnitX,Vector3.UnitY,1,1));
        Assert.False(EnbRetailLineJoin.WithinMergeAbortRaw(Vector3.Zero,new(2,0,0),Vector3.Zero,1,1));
    }

}
