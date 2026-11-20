using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbLineUvTests
{
    [Theory]
    [InlineData(0,1,0)]
    [InlineData(1,1,4)]
    [InlineData(2,0,4)]
    public void NativeMappingPreservesScrollAndBothEdges(int mode,float right,float end)
    {
        var asset=new EnbRetailTractorLineAsset(10,4,new(5,-2),new("line.tga","test.mix",0,1),
            new(0,"line.tga",new("line.tga","test.mix",0,1))){UvMode=mode};
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(0,0,100),Quaternion.Identity,new(800,600,1),
            Math.PI/2,new(1,10000),out var camera));
        var packet=asset.Build(1,1,1,Vector3.Zero,new(100,0,0),camera,250,1)!;
        var uv=packet.Mesh.MaterialPasses[0].TextureStages[0].EffectiveRuntimeTexCoords!;
        Vector2[] expected=[new(.25f,.5f),new(.25f+right,.5f),new(.25f,.5f+end),new(.25f+right,.5f+end)];
        Assert.Equal(4,uv.Count);
        for(int i=0;i<4;i++)Assert.True(Vector2.Distance(expected[i],uv[i])<.00001f);
    }

    [Fact]
    public void InstalledDefinitionsResolveDefaultAndExplicitMappings()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        Assert.Equal(0,EnbRetailTractorLineAsset.Load(assets,524).UvMode);
        Assert.Equal(1,EnbRetailTractorLineAsset.Load(assets,1886).UvMode);
        Assert.Equal(2,EnbRetailTractorLineAsset.Load(assets,1522).UvMode);
        Assert.Equal(0,EnbRetailTractorLineAsset.Load(assets,1897).Tile);
    }

    [Theory]
    [InlineData(-2,0)]
    [InlineData(0,0)]
    [InlineData(3,3)]
    [InlineData(256,256)]
    [InlineData(300,256)]
    public void NativeTileSetterClampsToItsClosedRange(int authored,int expected)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        assets.PresentationCatalog!.LoadBaseAssetsText($"[BASE-900001]\nWidth=5\nShader=1\nTexture=energ01.tga\nUVMode=1\nUVTile={authored}");
        var line=EnbRetailTractorLineAsset.Load(assets,900001);
        Assert.Equal(expected,line.Tile);
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(0,0,100),Quaternion.Identity,new(800,600,1),
            Math.PI/2,new(1,10000),out var camera));
        var packet=line.Build(1,1,1,Vector3.Zero,new(100,0,0),camera,0,1)!;
        var uv=packet.Mesh.MaterialPasses[0].TextureStages[0].EffectiveRuntimeTexCoords!;
        Assert.Equal(0,uv[0].Y);Assert.Equal(expected,uv[2].Y);
    }
}
