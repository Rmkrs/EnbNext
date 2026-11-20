using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // loads every installed procedural line BASE and avatar teleport beam through production loaders
public sealed class EnbInstalledLineAndBeamCorpusTests
{
    [Fact]
    public void InstalledLinesAndTeleportBeamsLoad()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var catalog = assets.PresentationCatalog!;
        Assert.True(EnbResolvedRenderCamera.TryCreate(new Vector3(0, 0, -500), Quaternion.Identity, new(800, 600, 1), Math.PI / 2, new(1, 100000), out var camera));
        var lines = new List<string>();
        var failures = new List<string>();
        int lineCount = 0, built = 0;
        for (int id = 0; id <= 32767; id++)
        {
            if (catalog.ResolveBaseAsset(id).Kind != EnbBaseAssetDefinitionKind.ProceduralWidth) continue;
            lineCount++;
            try
            {
                var line = EnbRetailTractorLineAsset.Load(assets, id);
                foreach (uint age in new uint[] { 0, 16, 1000 })
                    if (line.Build(1, 1, 1, new Vector3(-100, 0, 0), new Vector3(100, 0, 0), camera, age, 1) is not null) built++;
            }
            catch (Exception ex) { failures.Add($"line BASE-{id}: {ex.GetType().Name}: {ex.Message}"); }
        }
        lines.Add($"procedural lines {lineCount}, builds {built}");
        // Which line BASEs can an installed effect actually select?
        var referenced = new SortedDictionary<int, string>();
        for (int id = 0; id <= 32767; id++)
            if (catalog.FindEffect(id) is { BaseAssetId: int b } effect && catalog.ResolveBaseAsset(b).Kind == EnbBaseAssetDefinitionKind.ProceduralWidth)
            {
                var props = catalog.ResolveBaseAsset(b).Properties!;
                referenced[id] = $"E{id} {effect.EffectClass} BASE-{b} texture={props.GetValueOrDefault("Texture", "<absent>")}";
            }
        lines.Add($"effects selecting a procedural line BASE: {referenced.Count}, untextured: {referenced.Values.Count(v => v.EndsWith("<absent>"))}");
        lines.AddRange(referenced.Values.Where(v => v.EndsWith("<absent>")));
        foreach (var name in new[] { "av_tele01", "av_tele02", "av_tele03", "av_tele04" })
        {
            try { _ = new EnbStationBeamEffect(assets, name); lines.Add(name + " ok"); }
            catch (Exception ex) { failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}"); }
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-line-beam-corpus.txt"), [.. lines, "failures:", .. failures]);
        // Width-only BASEs (no Texture) are not selected by any installed effect.
        Assert.All(failures, f => Assert.Contains("Untextured line BASE-", f));
        Assert.DoesNotContain(lines, l => l.StartsWith("effects selecting") && !l.EndsWith("untextured: 0"));
    }
}
