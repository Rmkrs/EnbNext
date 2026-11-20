using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // reads installed Dazzle.ini and resolves every registered dazzle texture
public sealed class EnbInstalledDazzleTextureCorpusTests
{
    [Fact]
    public void EveryRegisteredDazzleLayerNamesATextureAndResolvesThroughTheNamedTextureClass()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var ini = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "Dazzle.ini"));
        Assert.True(ini.TryGetSection("Dazzles_List", out var list));
        var report = new List<string>();
        foreach (string type in list.Values.Select(v => v.Trim().Trim('"')))
        {
            var d = EnbDazzleDefinition.Resolve(ini, type);
            foreach (string name in new[] { d.Texture, d.HaloTexture }.Concat(d.Flares.Count > 0 ? [d.FlareTexture] : []))
            {
                // 0091DE00 returns no texture for an empty name; no installed layer is empty.
                Assert.False(string.IsNullOrWhiteSpace(name), type);
                var direct = assets.Textures.Resolve(name);
                var resolved = assets.Textures.ResolveCurrentRetailMissingTexture(name, assets.Paths.ClientExecutablePath);
                report.Add($"{type} {name} {direct.Status} {(resolved.Image is null ? "no-image" : "image")}");
                Assert.NotNull(resolved.Image);
            }
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-dazzle-texture-corpus.txt"), report);
    }
}
