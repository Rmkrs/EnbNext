using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbProceduralLightningTests
{
    [InstalledRetailFact]
    public void SelectedDetailLimitsSubmittedLightningTrianglesAfterCoalescing()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var line=EnbRetailTractorLineAsset.Load(assets,67);
        EnbResolvedRenderCamera.TryCreate(new(0,0,300),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        for(int level=0;level<=line.Subdivisions;level++)
        {
            var packet=line.Build(1,1,1,Vector3.Zero,new(300,0,0),camera,0,1,level)!;
            //009BF3A1..009C1E6E coalesces the subdivided strip. The selected
            //detail bounds submitted triangles; it does not fix their count.
            Assert.InRange(packet.Mesh.Indices.Count,3,6*(1<<level));
            Assert.Equal(0,packet.Mesh.Indices.Count%3);
        }
    }
    [InstalledRetailTheory]
    [InlineData(67)] [InlineData(453)] [InlineData(437)] [InlineData(439)] [InlineData(440)]
    [InlineData(442)] [InlineData(446)] [InlineData(529)] [InlineData(1059)] [InlineData(1885)]
    [InlineData(197)] [InlineData(2036)] [InlineData(2261)]
    public void InstalledLightningPreservesNoiseAndFrozenOrLiveRandomness(int id)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var line=EnbRetailTractorLineAsset.Load(assets,id);
        Assert.True(line.Noise>0);Assert.Null(line.RecoveryDiagnostic);
        EnbResolvedRenderCamera.TryCreate(new(0,0,300),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        var first=line.Build(1,1,1,Vector3.Zero,new(300,0,0),camera,0,1)!;
        var second=line.Build(1,1,1,Vector3.Zero,new(300,0,0),camera,0,1)!;
        // Native coalescing/zipper can remove triangles from the subdivided strip.
        Assert.InRange(first.Mesh.Indices.Count,3,6*(1<<line.Subdivisions));
        Assert.Equal(0,first.Mesh.Indices.Count%3);
        var positions=first.DeformedGeometry!.Positions;
        Assert.All(positions,p=>Assert.True(float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z)));
        Assert.Equal(line.Freeze,positions.SequenceEqual(second.DeformedGeometry!.Positions));
        Assert.All(first.Mesh.Indices,i=>Assert.InRange(i,0u,(uint)positions.Count-1));
    }

    [InstalledRetailTheory]
    [InlineData(519,4)] [InlineData(520,6)]
    public void EnergyProjectilesRetainTheirContiguousAuthoredPointChains(int id,int count)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var line=EnbRetailObjectLineAsset.Load(assets,id);
        Assert.Equal(count,line.Points.Count);
        if(id==519)Assert.Equal(line.Points[0],line.Points[^1]);
        else Assert.Equal(line.Points[1],line.Points[4]);
        EnbResolvedRenderCamera.TryCreate(new(0,0,300),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        var packet=line.Build(1,1,1,Matrix4x4.Identity,1,camera,0)!;
        Assert.InRange(packet.Mesh.Indices.Count,3,6*(count-1)*(1<<line.Line.Subdivisions));
        Assert.Equal(0,packet.Mesh.Indices.Count%3);
        var uv=packet.Mesh.MaterialPasses[0].TextureStages[0].EffectiveRuntimeTexCoords!;
        Assert.Equal((count-1)*line.Line.Tile,uv[^1].Y);
    }

    [InstalledRetailFact]
    public void PlasmaJaggedProjectileUsesNativeCoincidentDefaultsBeforeNoise()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var line=EnbRetailObjectLineAsset.Load(assets,442);
        Assert.Equal(new[]{Vector3.Zero,Vector3.Zero},line.Points);
        EnbResolvedRenderCamera.TryCreate(new(0,0,300),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        var packet=line.Build(1,1,1,Matrix4x4.Identity,1,camera,0)!;
        Assert.InRange(packet.Mesh.Indices.Count,3,96);
        Assert.Equal(0,packet.Mesh.Indices.Count%3);
        Assert.True(packet.DeformedGeometry!.Positions.Distinct().Count()>4);
    }

    [Fact]
    public void MultipleNullColorSubdivisionBatchesRenderAtBothFactoryScales()
    {
        Vector3[] points=[new(0,0,0),new(100,20,0),new(200,0,0),new(300,20,0)];
        EnbResolvedRenderCamera.TryCreate(new(0,0,300),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        //Corrected absent-color ownership retains all native batches.
        var first=EnbRetailProceduralLineGeometry.Build(points,camera,7,20,true,2,1,3,Vector2.Zero);
        EnbResolvedRenderCamera.TryCreate(new(0,0,600),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var doubled);
        var second=EnbRetailProceduralLineGeometry.Build(points.Select(p=>p*2).ToArray(),doubled,7,40,true,4,1,3,Vector2.Zero);
        foreach(var geometry in new[]{first,second})
        {
            Assert.NotEmpty(geometry.Indices);
            Assert.All(geometry.Positions,p=>Assert.True(float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z)));
            Assert.All(geometry.Indices,i=>Assert.True(i<geometry.Positions.Length));
        }
    }
}
