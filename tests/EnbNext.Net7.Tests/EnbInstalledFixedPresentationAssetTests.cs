using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // loads fixed presentation assets (bridges, map frame, warp plume/afterburn, warp tunnels) from the installed client
public sealed class EnbInstalledFixedPresentationAssetTests
{
    [Fact]
    public void InstalledFixedPresentationAssetsLoad()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var lines = new List<string>();
        var failures = new List<string>();
        void Try(string what, Action load)
        {
            try { load(); lines.Add(what + " ok"); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FileNotFoundException or KeyNotFoundException)
            { failures.Add($"{what}: {ex.GetType().Name}: {ex.Message}"); }
        }
        for (int race = 0; race < 3; race++) { int r = race; Try($"bridge race {r}", () => EnbRetailBridgeResource.Load(assets, r)); }
        Try("map frame", () => _ = new EnbAuthoredMapFrame(assets));
        Try("warp plume", () => EnbWarpPlumeAsset.Load(assets));
        Try("afterburn E674", () => EnbWarpPlumeAsset.LoadAttachEffect(assets, 674));
        var catalog = assets.PresentationCatalog!;
        var tunnels = new SortedSet<int>();
        var warpDefinitions = new List<string>();
        for (int id = 0; id <= 4096; id++)
            if (catalog.FindEffect(id) is { EffectClassKind: EnbEffectClassKind.WarpEffect, BaseAssetId: int asset } warp)
            {
                tunnels.Add(asset);
                warpDefinitions.Add($"warp E{id} BASE-{asset} " + string.Join(" ", warp.Properties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value)));
            }
        lines.AddRange(warpDefinitions);
        for (int id = 0; id <= 32767; id++)
            if (catalog.FindEffect(id) is { EffectClassKind: EnbEffectClassKind.AttachEffect } attach)
                lines.Add($"attach E{id} " + string.Join(" ", attach.Properties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value)));
        foreach (int asset in tunnels) Try($"warp tunnel BASE-{asset}", () => EnbWarpTunnelAsset.Load(assets, asset));
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-fixed-presentation-assets.txt"), [.. lines, "failures:", .. failures]);
        Assert.Empty(failures);
    }
}
