using EnbNext.Assets;
using EnbNext.Engine;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // decodes every installed music component
public sealed class EnbInstalledMusicCorpusTests
{
    [Fact]
    public void EveryInstalledMusicComponentDecodesToPcm16()
    {
        // The decoder's channel/sample and empty-output checks are reached only
        // by installed retail music; every music.ini component must pass them.
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var catalog = EnbRetailMusicCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory, "music.ini"));
        var components = catalog.Themes.SelectMany(t => t.Components)
            .DistinctBy(c => c.ResourceName, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.NotEmpty(components);
        var failures = components.Select(c => LiveWorldOpenAlAudio.DecodeMusic(assets, c))
            .Where(d => d.Error is not null || d.Channels is not (1 or 2) || d.Pcm.Length == 0)
            .Select(d => d.Component.ResourceName + ": " + d.Error).ToArray();
        Assert.Empty(failures);
    }
    [Fact]
    public void InstalledFrontendTrackDecodesToPcm16()
    {
        // UpdateFrontendMusic's decode-failure branch is reached only if the
        // installed fe_music.mp3 preset fails to decode.
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var preset = assets.Sounds!.GetDefinitionOrFile("fe_music.mp3")!;
        string resource = preset.Files.Single(f => !f.IsNullChoice).LogicalResourceName!;
        var decoded = LiveWorldOpenAlAudio.DecodeMusic(assets, new(resource, 0, 0));
        Assert.Null(decoded.Error);
        Assert.True(decoded.Channels is 1 or 2 && decoded.Pcm.Length > 0);
    }
}
