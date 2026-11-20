using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRingGeometryTests
{
    [Fact]
    public void NativeQuarterTurnsKeepPairedEllipticalEdgesAndSeam()
    {
        var mesh=EnbRetailRingGeometry.Build(4,new(2,3),new(5,7),2);
        Vector3[] expected=[new(0,3,0),new(0,7,0),new(-2,0,0),new(-5,0,0),
            new(0,-3,0),new(0,-7,0),new(2,0,0),new(5,0,0),new(0,3,0),new(0,7,0)];
        Assert.Equal(expected.Length,mesh.Positions.Length);
        for(int i=0;i<expected.Length;i++)
        {
            Assert.True(Vector3.Distance(expected[i],mesh.Positions[i])<.00001f);
            Assert.Equal(Vector3.UnitZ,mesh.Normals[i]);
            Assert.Equal(new Vector2((i/2)*.5f,i%2),mesh.TexCoords[i]);
        }
        Assert.Equal(new uint[]{0,1,2,1,2,3,2,3,4,3,4,5,4,5,6,5,6,7,6,7,8,7,8,9},mesh.Indices);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(16)]
    [InlineData(64)]
    public void RingTessellationKeepsNativeTopologyAndFiniteBounds(int segments)
    {
        var mesh=EnbRetailRingGeometry.Build(segments,new(1,2),new(3,4),0);
        Assert.Equal(2*(segments+1),mesh.Positions.Length);Assert.Equal(segments*6,mesh.Indices.Length);
        Assert.All(mesh.Indices,i=>Assert.InRange(i,0u,(uint)mesh.Positions.Length-1));
        Assert.All(mesh.TexCoords,p=>Assert.Equal(0,p.X));
        Assert.All(mesh.Positions,p=>{Assert.Equal(0,p.Z);Assert.InRange(p.X,-3,3);Assert.InRange(p.Y,-4,4);});
        Assert.True(Vector3.Distance(mesh.Positions[0],mesh.Positions[^2])<.0001f);
    }
}
