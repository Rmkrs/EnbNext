using System.Numerics;
using System.Reflection;
using EnbNext.Assets;
using EnbNext.Engine;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // builds the installed influence-room sky (BaseAsset 0x137) renderer
public sealed class EnbInstalledInfluenceSkyCorpusTests
{
    [Fact]
    public void InstalledInfluenceSkyRendererBuildsFromBaseAsset0x137()
    {
        // The influence renderer's resolve/texture/shader rejections are reached only
        // if the installed special-environment BaseAsset failed to build.
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var method = typeof(EnbWorldController).GetMethod("TryCreateInfluenceEnvironmentRenderer", BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] args = [assets, assets.NebulaEnvironment?.Between.SkyBoxColor, null, null, null];
        Assert.True((bool)method.Invoke(null, args)!, (string?)args[4]);
        ((IDisposable)args[2]!).Dispose();
    }
}
