using System.Numerics;
using EnbNext.Rendering;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbGalaxyMapPickingTests
{
    [Fact]
    public void SphereBroadPhaseStillRequiresBoxAndFiniteRay()
    {
        Vector4 sphere=new(0,0,0,2);
        Assert.True(EnbGalaxyMapPicking.Intersect(new(0,0,5),new(0,0,-5),sphere,new(-1),new(1),out var point));
        Assert.Equal(new Vector3(0,0,1),point);
        Assert.False(EnbGalaxyMapPicking.Intersect(new(1.5f,0,5),new(1.5f,0,-5),sphere,new(-1),new(1),out _));
        Assert.False(EnbGalaxyMapPicking.Intersect(new(0,0,5),new(0,0,1),sphere,new(-1),new(1),out _));
        Assert.True(EnbGalaxyMapPicking.Intersect(new(1,0,5),new(1,0,-5),sphere,new(-1),new(1),out _));
    }

    [Fact]
    public void LinkQueryUsesExpandedRectangleAndPerpendicularDistance()
    {
        Assert.Equal(1,EnbGalaxyMapPicking.LinkDistance(new(5,1,50),Vector3.Zero,new(10,0,0)));
        Assert.Equal(0,EnbGalaxyMapPicking.LinkDistance(new(5,0,0),Vector3.Zero,new(10,0,0)));
        Assert.Equal(-1,EnbGalaxyMapPicking.LinkDistance(new(12,1,0),Vector3.Zero,new(10,0,0)));
        Assert.Equal(1,EnbGalaxyMapPicking.LinkDistance(new(0,1,0),Vector3.Zero,Vector3.Zero));
    }
}
