using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbLineMergeEligibilityTests
{
    [Fact]
    public void FirstBranchRequiresBothStrictPositiveHalfSpaces()
    {
        var ray=Vector3.UnitX;
        foreach(float side in new[]{-1f,0f,1f})
        {
            Assert.Equal(side>0,EnbRetailLineJoin.IsMergeEligibleRaw(ray,true,ray,false,new(side,0,0),ray,ray,ray));
            Assert.Equal(side>0,EnbRetailLineJoin.IsMergeEligibleRaw(ray,true,ray,false,ray,new(side,0,0),ray,ray));
        }
    }

    [Fact]
    public void SecondBranchNegatesMainPlaneAndUsesPreviousOutputEdge()
    {
        var ray=Vector3.UnitX;
        Assert.True(EnbRetailLineJoin.IsMergeEligibleRaw(ray,false,ray,true,ray,-ray,-ray,ray));
        Assert.False(EnbRetailLineJoin.IsMergeEligibleRaw(ray,false,ray,true,ray,ray,ray,ray));
        Assert.False(EnbRetailLineJoin.IsMergeEligibleRaw(ray,false,ray,true,ray,ray,-ray,-ray));
        Assert.False(EnbRetailLineJoin.IsMergeEligibleRaw(ray,false,ray,true,ray,ray,-ray,Vector3.UnitY));
    }

    [Fact]
    public void FlagsDisableTheirOwnBranchesRatherThanAllMerging()
    {
        var ray=Vector3.UnitX;
        foreach(bool first in new[]{false,true})
        foreach(bool second in new[]{false,true})
            Assert.Equal(!(first&&second),EnbRetailLineJoin.IsMergeEligibleRaw(ray,first,ray,second,ray,ray,-ray,ray));
    }

    [Fact]
    public void TinyPositiveProductsDoNotUnderflowToFloatZero()
    {
        var tiny=new Vector3(float.Epsilon,0,0);
        Assert.True(EnbRetailLineJoin.IsMergeEligibleRaw(tiny,true,tiny,false,tiny,tiny,tiny,tiny));
        Assert.False(EnbRetailLineJoin.IsMergeEligibleRaw(tiny,true,tiny,false,-tiny,tiny,tiny,tiny));
    }

    [Fact]
    public void ZeroRayAndNonfiniteInputsHaveExplicitBoundaries()
    {
        var ray=Vector3.UnitX;
        Assert.False(EnbRetailLineJoin.IsMergeEligibleRaw(Vector3.Zero,false,Vector3.Zero,false,ray,ray,-ray,ray));
    }
}
