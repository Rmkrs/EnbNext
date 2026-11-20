using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSphereGeometryTests
{
    [Fact]
    public void EveryRetailLevelHasNativeCostAndConsistentInwardWinding()
    {
        Assert.Null(EnbRetailSphereGeometry.ForDetail(0));
        for(int level=1;level<=10;level++)
        {
            int n=level+6;
            var mesh=EnbRetailSphereGeometry.ForDetail(level)!;
            Assert.Equal((n+1)*n+2,mesh.Positions.Length);
            Assert.Equal(EnbSphereRingDetailBudgetSemantics.SphereLevelCosts[level],mesh.Indices.Length/3f);
            foreach(var normal in mesh.Normals)Assert.InRange(normal.Length(),.99999f,1.00001f);
            for(int i=0;i<mesh.Indices.Length;i+=3)
            {
                var a=mesh.Positions[mesh.Indices[i]];var b=mesh.Positions[mesh.Indices[i+1]];var c=mesh.Positions[mesh.Indices[i+2]];
                Assert.True(Vector3.Dot(Vector3.Cross(b-a,c-a),a+b+c)<0);
            }
            for(int row=0;row<n;row++)
            {
                int first=1+row*(n+1),last=first+n;
                Assert.InRange(Vector3.Distance(mesh.Positions[first],mesh.Positions[last]),0,1e-6f);
                Assert.Equal(0,mesh.TexCoords[first].X);Assert.Equal(1,mesh.TexCoords[last].X);
            }
        }
    }

    [Fact]
    public void CoordinatesAndCapOrderPreserveNativeAxes()
    {
        var mesh=EnbRetailSphereGeometry.Build(4,1);
        Assert.InRange(Vector3.Distance(new(0,-1,0),mesh.Positions[1]),0,1e-6f);
        Assert.InRange(Vector3.Distance(Vector3.UnitX,mesh.Positions[2]),0,1e-6f);
        Assert.Equal(new Vector2(.5f,0),mesh.TexCoords[0]);
        Assert.Equal(new Vector2(.5f,1),mesh.TexCoords[^1]);
        Assert.Equal(new uint[]{0,2,1,0,3,2,0,4,3,0,5,4,6,4,5,6,3,4,6,2,3,6,1,2},mesh.Indices);
    }
}
