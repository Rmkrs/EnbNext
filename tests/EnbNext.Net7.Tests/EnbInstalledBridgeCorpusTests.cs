using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // loads the three installed bridge models and their transition clips
public sealed class EnbInstalledBridgeCorpusTests
{
    [Fact]
    public void EveryInstalledBridgeHasEveryTransitionClipWithTiming()
    {
        // EnbBridgeTransitionRuntime.TryRequest refuses only a missing or untimed clip; every
        // installed bridge supplies all six transitions with frames and a frame rate.
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        foreach (int race in new[] { 0, 1, 2 })
        {
            var bridge = EnbRetailBridgeResource.Load(assets, race);
            Assert.Empty(bridge.AnimationLoadFailures);
            foreach (string suffix in new[] { "-ml", "-mr", "-mf", "-rl", "-lf", "-rf" })
            {
                var clip = bridge.Resource.Animations.FirstOrDefault(a => a.Name.Equals(bridge.Name + suffix, StringComparison.OrdinalIgnoreCase));
                Assert.True(clip is { NumFrames: > 0 } && clip.FrameRate != 0, bridge.Name + suffix);
            }
        }
    }
}
