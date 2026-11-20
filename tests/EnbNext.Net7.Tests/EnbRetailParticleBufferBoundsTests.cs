using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailParticleBufferBoundsTests
{
    [Fact]
    public void LifetimeExcludedKeyContributesOnlyItsInterpolatedEndpoint()
    {
        EnbCurrentRetailParticleScalarProperty size=new(2,[new(0,1),new(.5f,3),new(2,9)]);
        Assert.Equal(7,EnbRetailParticleBufferBounds.MaximumSize(size,1000));
        Assert.Equal(-14,EnbRetailParticleBufferBounds.MaximumSize(size,1000,-2));
        Assert.Equal(0,EnbRetailParticleBufferBounds.MaximumSize(size,1000,0));
    }

    [Fact]
    public void ConstructorKeepsEarlierMaximumAndConstantBranchIgnoresTinyRandom()
    {
        Assert.Equal(12,EnbRetailParticleBufferBounds.MaximumSize(new(-2,[new(0,10),new(.5f,1)]),1000));
        Assert.Equal(3,EnbRetailParticleBufferBounds.MaximumSize(new(1e-13f,[new(0,3)]),1000));
        Assert.Equal(5,EnbRetailParticleBufferBounds.MaximumSize(new(-2,[new(0,3)]),1000));
    }

    [Fact]
    public void EmptyBufferClearsBoundsAndSignedPaddingDoesNotClampExtents()
    {
        Assert.Equal(default,EnbRetailParticleBufferBounds.FromActivePositions([],20));
        var bounds=EnbRetailParticleBufferBounds.FromActivePositions([new(2,4,6)],-3);
        Assert.Equal(new Vector3(2,4,6),bounds.Center);
        Assert.Equal(new Vector3(-3),bounds.Extent);
        Assert.Equal(MathF.Sqrt(27),bounds.LocalSphere.Radius);
    }


    [Fact]
    public void DetailMetricSquaresSignedSize()
    {
        Assert.True(EnbRenderViewport.TryFromLogicalSize(1000,500,1,out var viewport));
        Assert.True(EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,viewport,
            Math.PI/2,new EnbRenderClipRange(1,10000),out var camera));
        var bounds=new EnbW3dWorldBounds(new(0,0,20),5);
        Assert.True(EnbParticleBufferDetailBudgetSemantics.TryComputeProjectedDetail(camera,bounds,10,2,0,out var positive));
        Assert.True(EnbParticleBufferDetailBudgetSemantics.TryComputeProjectedDetail(camera,bounds,10,-2,0,out var negative));
        Assert.Equal(positive,negative);
    }
}
