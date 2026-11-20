using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbNavigationRoutePlannerTests
{
    [Fact]
    public void BuildsConnectedRouteThroughIntermediateNavs()
    {
        EnbNavigationRoutePlanner.Node[] nodes = [new(1,Vector3.Zero,0),new(4,new(30,0,0),11),new(2,new(10,0,0),11),new(3,new(20,0,0),11)];
        Assert.Equal(new uint[] {2,3,4},EnbNavigationRoutePlanner.Build(nodes,0,0));
    }

    [Fact]
    public void UsesDestinationReachAndThreeDimensionalDistance()
    {
        EnbNavigationRoutePlanner.Node[] nodes = [new(1,Vector3.Zero,1000),new(3,new(0,0,20),11),new(2,new(0,0,10),11)];
        Assert.Equal(new uint[] {2,3},EnbNavigationRoutePlanner.Build(nodes,0,0));
    }

    [Fact]
    public void StrictBoundaryRequiresIntermediateUntilRangeExpands()
    {
        EnbNavigationRoutePlanner.Node[] nodes = [new(1,Vector3.Zero,0),new(3,new(20,0,0),20),new(2,new(10,0,0),11)];
        Assert.Equal(new uint[] {2,3},EnbNavigationRoutePlanner.Build(nodes,0,0));
        Assert.Equal(new uint[] {3},EnbNavigationRoutePlanner.Build(nodes[..2],0,0));
    }

    [Fact]
    public void EqualCostsKeepFirstDiscoveredParent()
    {
        EnbNavigationRoutePlanner.Node[] nodes = [new(1,Vector3.Zero,0),new(4,new(20,0,0),11),new(2,new(10,0,0),11),new(3,new(10,0,0),11)];
        Assert.Equal(new uint[] {2,4},EnbNavigationRoutePlanner.Build(nodes,0,0));
    }

    [Fact]
    public void InvalidPresentationCannotHangSearch()
    {
        EnbNavigationRoutePlanner.Node[] nodes = [new(1,Vector3.Zero,0),new(2,new(float.NaN,0,0),1)];
        Assert.Empty(EnbNavigationRoutePlanner.Build(nodes,0,0));
    }
}
