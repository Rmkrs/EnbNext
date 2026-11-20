using EnbNext.Assets;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbAvatarRunBindingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TogglePersistsRefreshesAndPreservesOtherCharacterOptions(bool existingRunOption)
    {
        var installed=EnbInstalledAssetLocator.TryLocate(); if(installed is null)return;
        string temporary=Path.Combine(Path.GetTempPath(),"EnbNext-avatar-run-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var paths=installed with {ClientOutputDirectory=temporary};
            var assets=EnbRetailAssetBundle.Load(paths); const uint character=12345;
            string path=Path.Combine(temporary,$"player{EnbRetailPlayerOptionsCatalog.GetCurrentCompatibilitySelector(character)}_options.ini");
            File.WriteAllText(path,"; retained\n"+(existingRunOption?"[BOOL-017]\nOptionName=Starbase_Default_Run\nOptionValue=yes\n":"")+
                "[FLOAT-001]\nOptionName=Sound_SFX\nOptionValue=42\n");
            Assert.True(assets.ResolvePlayerOptions(character).Options.StarbaseDefaultRun);
            assets.ToggleStarbaseDefaultRun(character);
            Assert.False(assets.ResolvePlayerOptions(character).Options.StarbaseDefaultRun);
            Assert.False(EnbRetailAssetBundle.Load(paths).ResolvePlayerOptions(character).Options.StarbaseDefaultRun);
            assets.SaveCinematicOptions(character,false,true);
            Assert.False(assets.ResolvePlayerOptions(character).Options.StarbaseDefaultRun);
            assets.ToggleStarbaseDefaultRun(character);
            var restored=EnbRetailAssetBundle.Load(paths).ResolvePlayerOptions(character).Options;
            Assert.True(restored.StarbaseDefaultRun);
            Assert.True(restored.StarbaseThirdPersonMode);
            assets.SaveStarbaseThirdPersonMode(character,false);
            Assert.False(assets.ResolvePlayerOptions(character).Options.StarbaseThirdPersonMode);
            Assert.False(EnbRetailAssetBundle.Load(paths).ResolvePlayerOptions(character).Options.StarbaseThirdPersonMode);
            assets.SaveStarbaseThirdPersonMode(character,true);
            Assert.True(EnbRetailAssetBundle.Load(paths).ResolvePlayerOptions(character).Options.StarbaseThirdPersonMode);
            Assert.False(restored.CameraWarp);
            Assert.Equal(42,restored.SoundSfx);
            Assert.Contains("; retained",File.ReadAllText(path));
            Assert.Throws<ArgumentException>(()=>assets.ToggleStarbaseDefaultRun(0));
        }
        finally { Directory.Delete(temporary,true); }
    }
}
