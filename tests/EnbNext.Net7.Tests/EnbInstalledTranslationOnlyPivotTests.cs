using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // reads installed UI hierarchies
public sealed class EnbInstalledTranslationOnlyPivotTests
{
    [Fact]
    public void TranslationOnlyConsumersNeverMeetARotatedPivotChain()
    {
        // EnbAuthoredTargetPanelLoader.TryResolveTranslation fails only for a rotated pivot
        // chain. Its translation-only consumers (scanner SCANNER_CLICK, the chat message-line
        // gap pivots and every loading-map pivot) use only identity chains in the installed
        // client; EnbAuthoredUiAsset falls back to the full hierarchy pose by design.
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var failures = new List<string>();
        void Check(string file, Func<string, bool> pivot)
        {
            var asset = EnbAuthoredUiAsset.Load(assets, file) ?? throw new InvalidDataException(file);
            foreach (var p in asset.Hierarchy.Pivots.Where(p => pivot(p.Name)))
                if (!EnbAuthoredTargetPanelLoader.TryResolveTranslation(asset.Hierarchy, p.Ordinal, out _)) failures.Add(file + ":" + p.Name);
        }
        Check("tsi08a.w3d", n => n == "SCANNER_CLICK");
        Check("tsi05d.w3d", n => n.Equals("chat_vp_br", StringComparison.OrdinalIgnoreCase) || n.Equals("vp1_br", StringComparison.OrdinalIgnoreCase));
        foreach (string file in new[] { "map_load00.w3d", "map_select.w3d", "map_from.w3d" }) Check(file, _ => true);
        Assert.True(failures.Count == 0, string.Join(", ", failures));
    }
}
