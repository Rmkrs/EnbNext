using EnbNext.Assets;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbBridgeModeStorageTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CameraModeUsesSharedRetailOptionsAndPreservesOtherValues(bool existing)
    {
        var installed = EnbInstalledAssetLocator.TryLocate();
        Assert.NotNull(installed);
        string temporary = Path.Combine(Path.GetTempPath(), "enb-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var paths = installed with { ClientOutputDirectory = temporary };
            var assets = EnbRetailAssetBundle.Load(paths);
            const uint character = 12345;
            string path = Path.Combine(temporary, $"player{EnbRetailPlayerOptionsCatalog.GetCurrentCompatibilitySelector(character)}_options.ini");
            if (existing) File.WriteAllText(path, "; retained\n[BOOL-017]\nOptionName=Bridge_Mode\nOptionValue=no\n[BOOL-018]\nOptionName=Camera_Warp\nOptionValue=no\n");
            Assert.False(assets.ResolvePlayerOptions(character).Options.BridgeMode);
            assets.SaveBridgeMode(character, true);
            Assert.True(assets.ResolvePlayerOptions(character).Options.BridgeMode);
            Assert.True(EnbRetailAssetBundle.Load(paths).ResolvePlayerOptions(character).Options.BridgeMode);
            assets.SaveBridgeMode(character, false);
            Assert.False(EnbRetailAssetBundle.Load(paths).ResolvePlayerOptions(character).Options.BridgeMode);
            if (existing)
            {
                Assert.False(assets.ResolvePlayerOptions(character).Options.CameraWarp);
                Assert.Contains("; retained", File.ReadAllText(path));
            }
            File.WriteAllText(path, File.ReadAllText(path).Replace("OptionName=Bridge_Mode\nOptionValue=no", "OptionName=Bridge_Mode\nOptionValue=yes").Replace("OptionName=Bridge_Mode\r\nOptionValue=no", "OptionName=Bridge_Mode\r\nOptionValue=yes"));
            Assert.True(EnbRetailAssetBundle.Load(paths).ResolvePlayerOptions(character).Options.BridgeMode);
            Assert.Throws<ArgumentException>(() => assets.SaveBridgeMode(0, true));
        }
        finally { Directory.Delete(temporary, true); }
    }
}
