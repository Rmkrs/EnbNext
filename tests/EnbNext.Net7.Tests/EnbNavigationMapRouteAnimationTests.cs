using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbNavigationMapRouteAnimationTests
{
    [Fact]
    public void CompletionResetsFractionAndAdvancesNextSegmentWithThisFramesDelta()
    {
        var animation=new EnbNavigationMapRouteAnimation();
        var sounds=new List<string>();
        Vector2? Resolve(int i)=>new Vector2((i+1)*10,0);
        var first=animation.Sample(Vector2.Zero,3,Resolve,260,false,sounds.Add);
        Assert.Equal(2,first.Length); Assert.Equal(5.2f,first[^1].X,5);
        var second=animation.Sample(Vector2.Zero,3,Resolve,250,false,sounds.Add);
        Assert.Equal(3,second.Length); Assert.Equal(15,second[^1].X);
        Assert.Equal(1,animation.CompletedSegments); Assert.Equal(.5f,animation.SegmentProgress);
        Assert.Equal(["starmap_build_1.wav"],sounds);
        var third=animation.Sample(Vector2.Zero,3,Resolve,250,false,sounds.Add);
        Assert.Equal(4,third.Length); Assert.Equal(25,third[^1].X);
        var last=animation.Sample(Vector2.Zero,3,Resolve,250,false,sounds.Add);
        Assert.Equal(30,last[^1].X); Assert.Equal(3,animation.CompletedSegments);
        Assert.Equal(["starmap_build_1.wav","starmap_build_1.wav","starmap_build_destination_1.wav"],sounds);
        animation.Sample(Vector2.Zero,3,Resolve,1000,false,sounds.Add);
        Assert.Equal(3,sounds.Count);
    }

    [Fact]
    public void LongFrameCompletesEachSegmentWithoutDividingElapsedAcrossThePath()
    {
        var animation=new EnbNavigationMapRouteAnimation(); var sounds=new List<string>();
        var points=animation.Sample(Vector2.Zero,3,i=>new Vector2(i+1,2*i),500,false,sounds.Add);
        Assert.Equal(4,points.Length); Assert.Equal(new Vector2(3,4),points[^1]);
        Assert.Equal(3,animation.CompletedSegments); Assert.Equal(0,animation.SegmentProgress);
        Assert.Equal(3,sounds.Count);
    }

    [Fact]
    public void WarpIsImmediateAndHiddenMissingTargetsInvalidatePreview()
    {
        var animation=new EnbNavigationMapRouteAnimation(); var sounds=new List<string>();
        var warp=animation.Sample(Vector2.Zero,3,i=>new Vector2(i+1,0),0,true,sounds.Add);
        Assert.Equal(4,warp.Length); Assert.Empty(sounds);
        int resolved=0;
        var invalid=animation.Sample(Vector2.Zero,3,i=> { resolved++; return i==1 ? null : new Vector2(10,0); },100,false,sounds.Add);
        Assert.Empty(invalid); Assert.Equal(2,resolved);
        animation.Reset(); Assert.Equal(0,animation.CompletedSegments); Assert.Equal(0,animation.SegmentProgress);
    }
}
