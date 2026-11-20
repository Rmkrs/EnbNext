using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // loads every installed effect BASE and poses its RObj model attachments
public sealed class EnbInstalledRObjModelAttachmentCorpusTests
{
    [Fact]
    public void InstalledEffectModelAttachmentsPose()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var catalog = assets.PresentationCatalog!;
        var bases = new SortedSet<int>();
        for (int id = 0; id <= 32767; id++)
            if (catalog.FindEffect(id) is { BaseAssetId: int b }) bases.Add(b);
        var lines = new List<string>();
        var failures = new List<string>();
        var loadFailures = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int loaded = 0, withModels = 0, poses = 0, sequenced = 0;
        foreach (int b in bases)
        {
            EnbCurrentRetailRObjEffectAsset asset;
            try { asset = EnbCurrentRetailRObjEffectAsset.Load(assets, b); loaded++; }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FileNotFoundException or KeyNotFoundException or NotSupportedException)
            {
                string key = ex.GetType().Name + ": " + ex.Message;
                key = key.Length > 120 ? key[..120] : key;
                loadFailures[key] = loadFailures.GetValueOrDefault(key) + 1;
                continue;
            }
            if (asset.ModelAttachments.Count == 0) continue;
            withModels++;
            if (asset.Sequence is not null) { sequenced++; continue; }
            foreach (var model in asset.ModelAttachments)
            {
                try
                {
                    EnbRObjModelAttachment.Validate(model.Asset);
                    foreach (uint now in new uint[] { 0, 250, 1000, 4000 }) { model.Pose(asset, Matrix4x4.Identity, 0, now); poses++; }
                }
                catch (Exception ex) { failures.Add($"BASE-{b} model {model.Name}: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        lines.Add($"effect BASEs {bases.Count}, loaded {loaded}, with model attachments {withModels} (sequenced {sequenced}), poses {poses}");
        lines.Add("load failures by message:");
        lines.AddRange(loadFailures.Select(p => $"{p.Value}\t{p.Key}"));
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-robj-model-corpus.txt"), [.. lines, "failures:", .. failures]);
        Assert.Empty(failures);
    }
}
