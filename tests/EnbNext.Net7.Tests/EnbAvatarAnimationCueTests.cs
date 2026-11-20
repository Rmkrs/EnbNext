using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbAvatarAnimationCueTests
{
    private static readonly EnbRetailAvatarBodyClock.Clip A=new("A",100,30),B=new("B",100,30);
    [Fact]
    public void CueBoundsAreStrictAndBothBlendSamplesDispatchInAuthoredOrder()
    {
        var catalog=EnbW3dAnimationSoundCatalog.Parse("[A]\nSound0=3,a3\nSound1=1,a1\nSound2=0,a0\n[B]\nSound0=1,b1,2D\n");
        var owner=new EnbRetailAvatarAnimationCues();
        Assert.Empty(owner.Submit(new(A,0,null,0,0),catalog));
        Assert.Equal(new[]{"a3","a1","b1"},owner.Submit(new(A,3,B,1,0),catalog).Select(c=>c.Sound.Sound));
        Assert.Empty(owner.Submit(new(A,3,B,1,1),catalog));
        // Pair release clears CC: promotion samples B from zero, not from
        // its former blend frame. This follows the manual HLOD union stores.
        Assert.Equal("b1",Assert.Single(owner.Submit(new(B,2,null,0,0),catalog)).Sound.Sound);
        Assert.Empty(owner.Submit(new(B,2,null,0,0),catalog));
    }
    [Fact]
    public void SingleToPairUsesLastAuthoredCueNotLastFrameForFirstHistory()
    {
        var catalog=EnbW3dAnimationSoundCatalog.Parse("[A]\nSound0=3,a3\nSound1=1,a1\n[B]\nSound0=1,b1\n");
        var owner=new EnbRetailAvatarAnimationCues();
        Assert.Equal(2,owner.Submit(new(A,4,null,0,0),catalog).Count);
        Assert.Equal(new[]{"a3","b1"},owner.Submit(new(A,5,B,1,.5f),catalog).Select(c=>c.Sound.Sound));
    }
    [Fact]
    public void MissingBoneAttributeUsesRetailRootDefault()
    {
        var catalog=EnbW3dAnimationSoundCatalog.Parse("[A]\nSound0=1,a\n");
        Assert.True(catalog.TryGet("A",out var track));Assert.Equal("root",track.BoneName);
    }
}
