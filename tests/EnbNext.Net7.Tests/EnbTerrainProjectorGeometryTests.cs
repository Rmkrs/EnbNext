using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbTerrainProjectorGeometryTests
{
    private static EnbRetailTerrain Terrain() => new(1, 4,
        Enumerable.Range(0, 16).Select(i => (ushort)i).ToArray(), 4, 2,
        "color", "detail", Vector2.One, false, 1, false, 0, "");

    [Fact]
    public void MissingLegacyDepthBiasUsesNativeOffsetAfterFootprintSelection()
    {
        var projection=EnbCurrentRetailShadowProjection.Create(Vector3.Zero,Vector3.One,
            Matrix4x4.CreateTranslation(0,0,30),-Vector3.UnitZ,64);
        var native=EnbRetailTerrainProjectorGeometry.BuildMesh(Terrain(),projection,true,
            new(0,"shadow.tga",null),new(1,"MultProjectorGradient.tga",null),.75f);
        var unsupported=EnbRetailTerrainProjectorGeometry.BuildMesh(Terrain(),projection,false,
            new(0,"shadow.tga",null),new(1,"MultProjectorGradient.tga",null),.75f);
        Assert.Equal(native.Indices,unsupported.Indices);
        Assert.Equal(1u,native.CurrentRetailDepthBias);
        Assert.Equal(0u,unsupported.CurrentRetailDepthBias);
        Assert.Equal(native.Positions.Select(p=>p+new Vector3(0,0,3)),unsupported.Positions);
        Assert.Equal(native.MaterialPasses[0].SingleBindingSemantics,unsupported.MaterialPasses[0].SingleBindingSemantics);
    }

    [Fact]
    public void SelectionIncludesExpandedEndpointsAndNativeTriangleWinding()
    {
        var vertices = EnbRetailTerrainProjectorGeometry.Build(Terrain(), Vector3.Zero,
            Vector3.Zero, Matrix4x4.Identity);
        Assert.Equal(54, vertices.Length); // cells 1..3 on both axes
        Assert.Equal(new Vector3(-1, -1, 10), vertices[0]);
        Assert.Equal(new Vector3(0, 0, 20), vertices[1]);
        Assert.Equal(new Vector3(-1, 0, 18), vertices[2]);
        Assert.Equal(vertices[0], vertices[3]);
        Assert.Equal(new Vector3(0, -1, 12), vertices[4]);
        Assert.Equal(vertices[1], vertices[5]);
        Assert.True(Vector3.Cross(vertices[1] - vertices[0], vertices[2] - vertices[0]).Z > 0);
    }

    [Fact]
    public void OutsideNegativeBoundaryTruncatesAndWrapsOnlyHeightSamples()
    {
        var vertices = EnbRetailTerrainProjectorGeometry.Build(Terrain(), new(-2.25f, -2.25f, 999),
            Vector3.Zero, Matrix4x4.Identity);
        Assert.Equal(24, vertices.Length); // trunc(-1.25)..trunc(.75) = -1..0
        Assert.Equal(new Vector3(-3, -3, 30), vertices[0]);
        Assert.Equal(new Vector3(-2, -2, 0), vertices[1]);
        Assert.Equal(new Vector3(-3, -2, 6), vertices[2]);
    }

    [Fact]
    public void OrientedDepthExtentContributesToTerrainFootprint()
    {
        var vertices = EnbRetailTerrainProjectorGeometry.Build(Terrain(), Vector3.Zero,
            new(0, 0, 2), new Matrix4x4(0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1));
        Assert.Equal(126, vertices.Length); // X -1..5, Y 1..3
        Assert.Equal(-3, vertices.Min(v => v.X));
        Assert.Equal(4, vertices.Max(v => v.X));
        Assert.Equal(-1, vertices.Min(v => v.Y));
        Assert.Equal(2, vertices.Max(v => v.Y));
    }
}
