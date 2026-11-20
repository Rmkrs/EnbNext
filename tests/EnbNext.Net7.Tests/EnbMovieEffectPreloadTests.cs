using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // retail/hardware verification, >=5 s; run with --filter Category=Heavy
public sealed class EnbMovieEffectPreloadTests
{
    [Fact]
    public void RetainedFactoryPrecedesUnavailableFileAndReceivesCombinedInputs()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        assets.PresentationCatalog!.LoadBaseAssetsText("[BASE-31084]\nNAME=generated-no-file\nFILE=absent-generated.w3d\n");
        assets.PresentationCatalog.LoadEffectsText("[EFFECT-31084]\nEffectClass=RObjEffect\nBaseAssetID=31084\nRObjScale=3\nRObjHSV=60,0,0\n");
        using var movie=new EnbRetailMovieAssets(assets);
        var retained=new EnbCurrentRetailRObjEffectAsset(null,null,false,null,null){IsPreconfiguredFactoryObject=true};
        int acquisitions=0,constructors=0;
        movie.RetainedObjectEffectFactory=(definition,scale,hsv)=>
        {
            acquisitions++;Assert.Equal(6,scale);Assert.Equal(new Vector3(120,.1f,-.1f),hsv);
            return new(null,()=>retained);
        };
        movie.ObjectEffectPreloader=(_,asset,_,_)=>{constructors++;Assert.Same(retained,asset);};
        Assert.False(movie.Effect("CREATE_SFX_ON_OBJECT",new("1,31084,1,2,60,0.1,-0.1"),[],true,0));
        Assert.Equal(1,acquisitions);Assert.Equal(1,constructors);
        Assert.Null(movie.Diagnostic);Assert.Empty(movie.Effects);
    }

    [Theory]
    [InlineData("RObjEffect",false)] [InlineData("AttachEffect",false)]
    [InlineData("WarpEffect",true)]
    public void ObjectPreloadForwardsConstructedResourceAndFactoryInputsWithoutActivation(string kind,bool fail)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        assets.PresentationCatalog!.LoadEffectsText($"[EFFECT-31083]\nEffectClass={kind}\nBaseAssetID=377\nRObjScale=3\nRObjHSV=60,0,0\nResetMappers=true\n");
        using var movie=new EnbRetailMovieAssets(assets);
        int calls=0,deadlines=0;
        movie.ObjectEffectPreloader=(definition,asset,scale,hsv)=>
        {
            calls++;Assert.NotNull(asset.Resource);Assert.NotEmpty(asset.Resource!.Meshes);
            Assert.Equal(6,scale);Assert.Equal(new Vector3(120,.1f,-.1f),hsv);
            Assert.True(EnbRetailIniDocument.ReadBool(definition.Properties,"ResetMappers"));
            if(fail)throw new NotSupportedException("Local mapper control");
        };
        movie.EffectDeadline=(_,_)=>deadlines++;
        Assert.False(movie.Effect("CREATE_SFX_ON_OBJECT",new("1,31083,1,2,60,0.1,-0.1"),[],true,0));
        Assert.Equal(1,calls);Assert.Equal(0,deadlines);Assert.Empty(movie.Effects);
        if(fail)Assert.Contains("Local mapper control",movie.Diagnostic);else Assert.Null(movie.Diagnostic);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JetPreloadUsesItsConstructorAndContainsFailureWithoutActivation(bool fail)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        assets.PresentationCatalog!.LoadEffectsText("[EFFECT-31083]\nEffectClass=ParticleJetEffect\nBaseAssetID=1583\nRObjScale=3\nRObjHSV=60,0,0\n");
        using var movie=new EnbRetailMovieAssets(assets);
        int constructors=0,deadlines=0;
        movie.ParticleJetEffectPreloader=(definition,scale,hsv)=>
        {
            constructors++;
            Assert.Equal(6,EnbCurrentRetailParticleJetScale.Read(definition,scale));
            Assert.True(EnbCurrentRetailEffectFactoryHsv.TryRead(definition.Properties,hsv,out var sum,out _));
            Assert.Equal(new Vector3(120,.1f,-.1f),sum);
            if(fail)throw new InvalidDataException("Local constructor control");
        };
        movie.EffectDeadline=(_,_)=>deadlines++;
        Assert.False(movie.Effect("CREATE_SFX_BETWEEN_OBJECTS",new("1,2,31083,1,2,60,0.1,-0.1"),[],true,0));
        Assert.Equal(1,constructors);Assert.Equal(0,deadlines);Assert.Empty(movie.Effects);
        if(fail)Assert.Contains("Local constructor control",movie.Diagnostic);else Assert.Null(movie.Diagnostic);
    }

    [Theory]
    [InlineData("SplineRandomSeekEffect", "0", 2u, true, "0.4", "0")]
    [InlineData("LinearRandomSeekEffect", "0", 2u, true, "0.4", "0")]
    [InlineData("SplineRandomSeekEffect", "0.6", 1u, true, "0.4", "0")]
    [InlineData("LinearRandomSeekEffect", "0.6", 1u, true, "0.4", "0")]
    [InlineData("SplineRandomSeekEffect", "0", 0u, false, "0.4", "0")]
    [InlineData("LinearRandomSeekEffect", "0", 0u, false, "0.4", "0")]
    [InlineData("SplineRandomSeekEffect", "0", 11u, true, "-1", "0")]
    [InlineData("SplineRandomSeekEffect", "0", 1u, true, "1e20", "0")]
    [InlineData("SplineRandomSeekEffect", "0.6", 4u, true, "0.4", "2")]
    [InlineData("LinearRandomSeekEffect", "0.6", 4u, true, "0.4", "2")]
    public void SeekPreloadConstructsTablesOnlyAfterSuccessfulBaseFactory(string kind,string duration,uint words,bool available,string changeTime,string cycle)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        using var cache=EnbW3dRenderResourceCache.ForAssetNamespace(assets);
        if(available)
        {
            var bytes=EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("M","NULL",[("MS03A","ROOT")],25,0);
            Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var file,out var error),error);
            var source=new EnbResolvedW3dResource("preload-seek.w3d","M",new("preload-seek.w3d","fixture",0,bytes.Length),file.Model,[]){PrototypeFile=file};
            Assert.True(cache.TryLoadFile(source,out _,out error),error);
        }
        assets.PresentationCatalog!.LoadBaseAssetsText("[BASE-31083]\nNAME=M\nFILE=unavailable-preload-seek.w3d\n");
        assets.PresentationCatalog.LoadEffectsText($"[EFFECT-31083]\nEffectClass={kind}\nBaseAssetID=31083\nDuration=1.2\nChangeTime={changeTime}\nCycleTime={cycle}\nTimeToChange=0.1\nDistance=10\nOneChange=1\n"+(available?"":"RObjScale=invalid\n"));
        using var movie=new EnbRetailMovieAssets(assets);
        var random=kind=="SplineRandomSeekEffect"?EnbRetailSeekRandomStreams.Process.Spline:EnbRetailSeekRandomStreams.Process.Linear;
        lock(random)
        {
            uint before=random.Counter;
            Assert.False(movie.Effect("CREATE_SFX_BETWEEN_OBJECTS",new($"1,2,31083,{duration}"),[],true,0));
            Assert.Equal(unchecked(before+words),random.Counter);
        }
        if(available)Assert.Null(movie.Diagnostic);else Assert.NotNull(movie.Diagnostic);
        Assert.Empty(movie.Effects);
    }

    [Theory]
    [InlineData("2,60,0,0",3,60,6,120)]
    [InlineData("",2,0,2,0)]
    [InlineData("1,0,0,0",1,120,1,120)]
    public void PreloadCombinesCommandAndDefinitionInputsBeforeVariantSelectionWithoutAdmittingEffect(
        string arguments,float authoredScale,float authoredHue,float expectedScale,float expectedHue)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        using var cache=EnbW3dRenderResourceCache.ForAssetNamespace(assets);
        string variant=EnbCurrentRetailPrototypeVariantName.Create("M",expectedScale,new(expectedHue,0,0));
        Assert.True(variant.Length<16);
        var bytes=EnbR6Z11CurrentRetailAggregateTests.BuildAggregate(variant,"NULL",[("MS03A","ROOT")],25,0);
        Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var file,out var error),error);
        var source=new EnbResolvedW3dResource("preload-variant.w3d",variant,new("preload-variant.w3d","fixture",0,bytes.Length),file.Model,[]){PrototypeFile=file};
        Assert.True(cache.TryLoadFile(source,out _,out error),error);
        Assert.False(cache.TryFindPrototypeSource("MS03A",out _));
        assets.PresentationCatalog!.LoadBaseAssetsText("[BASE-31082]\nNAME=M\nFILE=unavailable-preload-base.w3d\n");
        assets.PresentationCatalog.LoadEffectsText(FormattableString.Invariant($"[EFFECT-31082]\nEffectClass=RObjEffect\nBaseAssetID=31082\nRObjScale={authoredScale}\nRObjHSV={authoredHue},0,0\n"));
        using var movie=new EnbRetailMovieAssets(assets);
        int deadlines=0;movie.EffectDeadline=(_,_)=>deadlines++;
        Assert.False(movie.Effect("CREATE_SFX_ON_OBJECT",new("1,31082,1"+(arguments.Length==0?"":","+arguments)),[],true,0));
        Assert.Null(movie.Diagnostic);
        //Only the selected variant owns MS03A. Ordinary NAME/FILE fallback
        //cannot load it; child publication also proves actual construction.
        Assert.True(cache.TryFindPrototypeSource("MS03A",out _));
        Assert.Empty(movie.Effects);
        Assert.Equal(0,deadlines);
    }
}
