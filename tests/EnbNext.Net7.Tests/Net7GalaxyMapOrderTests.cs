using EnbNext.Net7.Protocol;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class Net7GalaxyMapOrderTests
{
    [Fact]
    public void SourcePrependAndViewPrependHaveDifferentCollisionOrder()
    {
        var source=Net7GalaxyMapOrder.Empty.Retain([0,193,386,1]);
        Assert.Equal(new uint[]{386,193,0,1},source.Keys);
        Assert.Equal(new uint[]{0,193,386,1},Net7GalaxyMapOrder.Empty.Retain(source.Keys).Keys);
        var removed=source.Retain([0,386,1]);
        Assert.Equal(new uint[]{386,0,1},removed.Keys);
        Assert.Equal(new uint[]{193,386,0,1},removed.Retain([0,386,1,193]).Keys);
    }

    [Fact]
    public void GrowthMovesOldHeadsAndRemovalDoesNotShrinkBuckets()
    {
        uint[] keys=[0,389,..Enumerable.Range(1,192).Select(i=>(uint)i)];
        var grown=Net7GalaxyMapOrder.Empty.Retain(keys);
        Assert.Equal(389u,grown.BucketCount);
        Assert.Equal(new uint[]{389,0},grown.Keys.Take(2));
        var reduced=grown.Retain([0,389]);
        Assert.Equal(389u,reduced.BucketCount);
        Assert.Equal(new uint[]{389,0},reduced.Keys);
    }
}
