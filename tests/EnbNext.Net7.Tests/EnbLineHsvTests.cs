using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbLineHsvTests
{
    [Theory]
    [InlineData(360,90,true)] [InlineData(360,90,false)]
    [InlineData(-360,-90,true)] [InlineData(-360,-90,false)]
    [InlineData(360,540,true)] [InlineData(360,540,false)]
    public void FactoryRetainsFiniteCombinedHueBeyondOneTurn(float authored,float incoming,bool prototype)
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText(FormattableString.Invariant($"[EFFECT-1]\nEffectClass=LineEffect\nDuration=1\nRObjHSV={authored},0,0"));
        var root=EnbR10EffectSoundTests.Root() with {GeometryKind=EnbSceneEffectGeometryKind.ObjectToObject,IsObjectToObject=true,HsvShift=new(incoming,0,0)};
        var node=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0).VisualNodes);
        Assert.True(EnbCurrentRetailEffectFactoryHsv.TryRead(node,out var actual,out var error,prototype),error);
        Assert.Equal(new Vector3(authored+incoming,0,0),actual);
        //004904B0 wraps once; sectors outside0..5 leave the RGB unchanged.
        Assert.InRange(Vector3.Distance(actual.X==450?new Vector3(.5f,1,0):Vector3.UnitX,
            EnbCurrentRetailHsvRuntime.ShiftRgb(Vector3.UnitX,actual)),0,.000001f);
        Assert.NotEqual(EnbCurrentRetailAssetVariantName.CreateTexture("source.tga",new(actual.X%360,0,0)),
            EnbCurrentRetailAssetVariantName.CreateTexture("source.tga",actual));
    }

    [Fact]
    public void LineFactoryCombinesAuthoredAndRootShiftWithoutPrototypeThreshold()
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-1]\nEffectClass=LineEffect\nDuration=1\nRObjHSV=180,.2,-.1");
        var root=EnbR10EffectSoundTests.Root() with {GeometryKind=EnbSceneEffectGeometryKind.ObjectToObject,IsObjectToObject=true,HsvShift=new(-179.5f,-.2f,.105f)};
        var node=Assert.Single(new EnbCurrentRetailEffectRslSchedulerRuntime().Update([root],catalog,0).VisualNodes);
        Assert.True(EnbCurrentRetailEffectFactoryHsv.TryRead(node,out var line,out _,applyPrototypeThreshold:false));
        Assert.Equal(.5f,line.X); Assert.Equal(0,line.Y); Assert.Equal(.005f,line.Z,6);
        Assert.True(EnbCurrentRetailEffectFactoryHsv.TryRead(node,out var prototype,out _));
        Assert.Equal(Vector3.Zero,prototype);
    }

    [Fact]
    public void InstalledBeamVariantsKeepDistinctTexturePixelsAndOriginalAsset()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var original=EnbRetailTractorLineAsset.Load(assets,1886);
        var emp=EnbRetailTractorLineAsset.Load(assets,1886,new(150,0,0));
        var other=EnbRetailTractorLineAsset.Load(assets,1886,new(-90,0,0));
        Assert.NotEqual(original.Key,emp.Key); Assert.NotEqual(emp.Key,other.Key);
        Assert.Equal(original.Width,emp.Width); Assert.Equal(original.Tile,emp.Tile); Assert.Equal(original.Rate,emp.Rate);
        Assert.Equal(original.Texture.Location,emp.Texture.Location);
        var raw=assets.Textures.Resolve(original.Texture.LogicalName).Image!;
        var shifted=assets.Textures.Resolve(emp.Texture.LogicalName).Image!;
        Assert.False(raw.Pixels.Span.SequenceEqual(shifted.Pixels.Span));
        Assert.NotEqual(emp.Texture.LogicalName,other.Texture.LogicalName);
        Assert.Equal(Vector4.One,original.Color); Assert.Equal(original.Color,emp.Color);
    }

    [Fact]
    public void TinyShiftChangesLineRgbButLeavesTextureUnchanged()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var original=EnbRetailTractorLineAsset.Load(assets,1091);
        var shifted=EnbRetailTractorLineAsset.Load(assets,1091,new(.5f,0,-.005f));
        Assert.Equal(original.Texture.LogicalName,shifted.Texture.LogicalName);
        Assert.NotEqual(original.Color,shifted.Color);
        Assert.Equal(original.Color.W,shifted.Color.W);
    }
}
