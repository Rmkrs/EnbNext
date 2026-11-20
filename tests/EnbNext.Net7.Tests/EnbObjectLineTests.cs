using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbObjectLineTests
{
    [Theory]
    [InlineData(519)] [InlineData(520)] [InlineData(442)] [InlineData(443)]
    public void ObjectLineBudgetUsesLocalBoundsAndAuthoredSegmentCosts(int id)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var line=EnbRetailObjectLineAsset.Load(assets,id);
        var world=Matrix4x4.CreateRotationZ(.7f)*Matrix4x4.CreateTranslation(20,40,300);
        var local=line.Bounds(Matrix4x4.Identity,2);
        var transformed=line.Bounds(world,2);
        Assert.Equal(local.Radius,transformed.Radius);
        Assert.Equal(Vector3.Transform(local.Center,world),transformed.Center);
        Assert.Equal(line.Bounds(Matrix4x4.Identity,1).Radius*2,local.Radius);
        EnbResolvedRenderCamera.TryCreate(new(0,0,600),Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        for(int level=0;level<=line.Line.Subdivisions;level++)
        {
            var packet=line.Build(1,1,1,world,2,camera,0,level);
            if(id==442 && level==0)
            {
                // Native emits the coincident centerline as zero-area geometry.
                Assert.All(packet!.DeformedGeometry!.Positions,p=>Assert.Equal(packet.DeformedGeometry.Positions[0],p));
                continue;
            }
            // The detail budget counts the subdivided strip before009C0BC2
            // removes independent edge intersections. It bounds emitted work.
            Assert.InRange(packet!.Mesh.Indices.Count,3,
                3*(int)EnbSegLineDetailBudgetSemantics.CurrentRetailLevelCost((uint)line.Points.Count,level));
            Assert.All(packet.DeformedGeometry!.Positions,p=>Assert.True(float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z)));
        }
    }
    [Theory]
    [InlineData(443,20f)]
    [InlineData(445,50f)]
    [InlineData(1568,50f)]
    public void InstalledProjectileKeepsAuthoredPointsAndBuildsAtEffectPose(int id,float length)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var asset=EnbCurrentRetailRObjEffectAsset.Load(assets,id);
        var line=Assert.IsType<EnbRetailObjectLineAsset>(asset.ObjectLine);
        Assert.Equal(Vector3.Zero,line.Start);Assert.Equal(new Vector3(length,0,0),line.End);
        EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        var world=Matrix4x4.CreateRotationZ(.4f)*Matrix4x4.CreateTranslation(0,0,300);
        var packet=Assert.IsType<EnbW3dDrawTask>(line.Build(1,1,1,world,2,camera,100));
        Assert.Equal(4,packet.DeformedGeometry!.Positions.Count);
        Assert.Equal(6,packet.Mesh.Indices.Count);
        Assert.All(packet.DeformedGeometry.Positions,p=>Assert.True(float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z)));
        var direct=line.Line.Build(1,1,1,Vector3.Transform(line.Start*2,world),Vector3.Transform(line.End*2,world),camera,100,2)!;
        Assert.Equal(direct.DeformedGeometry!.Positions.ToArray(),packet.DeformedGeometry.Positions.ToArray());
        Assert.NotNull(EnbCurrentRetailRObjEffectAsset.Load(assets,id,new(120,.1f,0)).ObjectLine);
    }

    [Theory]
    [InlineData("Point0=0,0,0", "Point1")]
    [InlineData("Point0=NaN,0,0\nPoint1=1,0,0", "Point0")]
    public void IncompleteOrUnmodeledAuthoredPointChainsStayExplicit(string points,string expected)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        assets.PresentationCatalog!.LoadBaseAssetsText("[BASE-900001]\nWidth=5\nShader=1\nTexture=energ01.tga\n"+points);
        Assert.Contains(expected,Assert.Throws<InvalidDataException>(()=>EnbRetailObjectLineAsset.Load(assets,900001)).Message);
    }
}
