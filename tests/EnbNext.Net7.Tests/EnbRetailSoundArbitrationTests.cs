using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailSoundArbitrationTests
{
    [Fact]
    public void ArrivalReplacesStrictlyLowerScoreAndRetainsLogicalVictim()
    {
        var arb = new EnbRetailSoundArbitration();
        var far = arb.Start("engine",1,1,.9f,.1f);
        var near = arb.Start("ENGINE",1,1,.1f,.9f);
        Assert.False(far.Audible); Assert.True(near.Audible);
        Assert.Null(arb.Recover());
        arb.Remove(near);
        Assert.Same(far,arb.Recover()); Assert.True(far.Audible);
    }

    [Fact]
    public void EqualScoreDoesNotReplaceEvenWithHigherPriority()
    {
        var arb = new EnbRetailSoundArbitration();
        var first=arb.Start("sfx",1,1,.1f,.5f);
        var second=arb.Start("sfx",1,1,1,.5f);
        Assert.True(first.Audible); Assert.False(second.Audible);
        first.Score=0;
        Assert.Null(arb.Recover()); // recovery cannot preempt at a full definition
    }

    [Fact]
    public void RecoverySelectsOneHighestPositivePriorityWithStableTies()
    {
        var arb = new EnbRetailSoundArbitration();
        var owner=arb.Start("sfx",1,1,.5f,1);
        var low=arb.Start("sfx",1,1,.1f,0);
        var high=arb.Start("sfx",1,1,.9f,0);
        var tie=arb.Start("sfx",1,1,.9f,0);
        arb.Remove(owner);
        Assert.Same(high,arb.Recover()); Assert.False(low.Audible); Assert.False(tie.Audible);
        arb.Remove(high); Assert.Same(tie,arb.Recover());
    }

    [Fact]
    public void AuthoredPriorityIsClampedBeforeRecoveryRanking()
    {
        var arb=new EnbRetailSoundArbitration();
        var owner=arb.Start("sfx",1,1,1,1);
        var first=arb.Start("sfx",1,1,1,0);
        var excess=arb.Start("sfx",1,1,2,0);
        var negative=arb.Start("sfx",1,1,-1,0);
        arb.Remove(owner);
        Assert.Same(first,arb.Recover());
        Assert.Equal(1,excess.Priority); Assert.Equal(0,negative.Priority);
    }

    [Fact]
    public void SpatialRankingDoesNotUsePlaybackGainOrEuclideanApproximation()
    {
        Assert.Equal(1,EnbRetailSoundArbitration.SpatialScore(Vector3.Zero,100));
        Assert.Equal(.5f,EnbRetailSoundArbitration.SpatialScore(new(50,0,0),100));
        Assert.Equal((float)(1-(10+3*.34375+2*.25)/100),EnbRetailSoundArbitration.SpatialScore(new(10,3,2),100));
        Assert.Equal((float)(1-(10+3*.34375+3*.25)/100),EnbRetailSoundArbitration.SpatialScore(new(10,2,3),100));
    }
}
