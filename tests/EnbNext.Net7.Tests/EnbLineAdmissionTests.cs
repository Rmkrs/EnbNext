using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbLineAdmissionTests
{
    [Fact]
    public void InstalledSuffixedNoiseCannotSilentlyBecomeAStraightLine()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        Assert.Equal("4.0F",assets.PresentationCatalog!.ResolveBaseAsset(437).Properties!["Noise"]);
        var line=EnbRetailTractorLineAsset.Load(assets,437);
        Assert.Equal(4f,line.Noise);Assert.Equal(5,line.Subdivisions);
        Assert.Null(line.RecoveryDiagnostic);
    }

    [Theory]
    [InlineData("Noise","invalid")]
    [InlineData("Noise","NaN")]
    [InlineData("UVTile","invalid")]
    [InlineData("Opacity","invalid")]
    public void ExplicitUnsupportedNumbersDoNotUseMissingKeyDefaults(string key,string value)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        assets.PresentationCatalog!.LoadBaseAssetsText($"[BASE-900001]\nWidth=5\nShader=1\nTexture=energ01.tga\n{key}={value}");
        var error=Assert.Throws<InvalidDataException>(()=>EnbRetailTractorLineAsset.Load(assets,900001));
        Assert.Contains(key,error.Message,StringComparison.OrdinalIgnoreCase);
    }

    // 004983A0 reads Shader/UVMode/SubDivs with 009CA1C0, maps any Shader to a
    // 0093B2xx preset, stores SubDivs unsigned clamped above 6 (00969380) and
    // |Noise| (00969340).
    [Fact]
    public void IntegerKeysFollowTheRetailReaderAndSetters()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        assets.PresentationCatalog!.LoadBaseAssetsText("[BASE-900002]\nWidth=5\nShader=9\nTexture=energ01.tga\nSubDivs=-1\nUVMode=invalid\nNoise=-2");
        var line=EnbRetailTractorLineAsset.Load(assets,900002);
        Assert.Equal(9,line.ShaderPreset);Assert.Equal(7,line.Subdivisions);Assert.Equal(0,line.UvMode);Assert.Equal(2f,line.Noise);
    }
}
