using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // builds the RObj factory of every installed BASE definition
public sealed class EnbInstalledBaseFactoryCorpusTests
{
    [Fact]
    public void EveryInstalledBaseFactoryBuildsOrFailsOnlyAsListed()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var catalog = assets.PresentationCatalog!;
        var failures = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        int defined = 0, built = 0, rings = 0, spheres = 0, lines = 0;
        for (int id = 0; id <= 32767; id++)
        {
            var definition = catalog.ResolveBaseAsset(id);
            if (definition.Kind is not (EnbBaseAssetDefinitionKind.FileName or EnbBaseAssetDefinitionKind.ProceduralWidth or EnbBaseAssetDefinitionKind.NullObject)) continue;
            defined++;
            try
            {
                var asset = EnbCurrentRetailRObjEffectAsset.Load(assets, id);
                built++;
                if (asset.Rings is not null) rings++;
                if (asset.Spheres is not null) spheres++;
                if (asset.ObjectLine is not null) lines++;
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FileNotFoundException or KeyNotFoundException or NotSupportedException or ArgumentException)
            {
                string key = ex.GetType().Name + ": " + System.Text.RegularExpressions.Regex.Replace(ex.Message, "[0-9]+", "N");
                key = key.Length > 140 ? key[..140] : key;
                (failures.TryGetValue(key, out var ids) ? ids : failures[key] = []).Add(id);
            }
        }
        var report = new List<string> { $"defined {defined}, built {built}, rings {rings}, spheres {spheres}, object lines {lines}", "failures by message:" };
        report.AddRange(failures.Select(p => $"{p.Value.Count}\t{p.Key}\t{string.Join(',', p.Value.Take(12))}"));
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-base-factory-corpus.txt"), report);
        // Only the untextured Width-only line BASEs (retail leaves their texture
        // null; no installed effect selects one) and the listed mesh-HLOD gap fail.
        Assert.True(rings > 0 && spheres > 0 && lines > 0);
        Assert.All(failures.Keys, key => Assert.True(key.Contains("Untextured line BASE-N") || key.Contains("locally bound mesh HLOD"), key));
    }
}
