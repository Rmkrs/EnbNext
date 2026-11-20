using System.Diagnostics;
using System.Text.RegularExpressions;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // prepares every installed land####.ini through the production terrain preparation
public sealed class EnbInstalledTerrainCorpusTests
{
    // The CPU part of LiveWorldTerrain.AppendTerrainPackets after preparation.
    private static string BuildLikeLiveWorldTerrain(EnbRetailPreparedTerrain prepared, EnbRetailAssetBundle assets,
        EnbMixResourceCatalog resources, EnbW3dRenderResourceCache prototypes)
    {
        EnbW3dRenderTextureLocation Texture(string name)
        {
            if (assets.Paths.TryResolveRetailLooseResource(name, out string path)) return new(name, path, 0, new FileInfo(path).Length);
            if (!resources.TryLocateResource(name, out var location)) throw new FileNotFoundException($"Terrain texture missing: {name}");
            return new(location.Name, location.ArchivePath, location.DataOffset, location.DataLength);
        }
        var terrain = prepared.Terrain;
        _ = new EnbRetailTerrainLightmap(terrain, prepared.Prelight, prepared.Color, prepared.ColorAlphaIsIllumination, terrain.LightmapChunk);
        var surface = new EnbRetailTerrainSurface(terrain.Copy().SetColorMap($"enbnext-land{terrain.Sector}-lit"), Texture(terrain.ColorMap), Texture(terrain.DetailMap));
        var decorations = prepared.Decorations ?? throw new InvalidDataException(prepared.DecorationDiagnostic ?? "Decoration preparation failed.");
        var selected = new List<EnbRetailTerrainDecorationPrototype>();
        var random = EnbRetailCrtRandomSource.Current;
        using (var factory = new EnbRetailTerrainShadowObjectFactory(resources, prototypes, 0, random.State,
            new EnbRetailFileResolver(Path.GetDirectoryName(assets.Paths.ClientExecutablePath)!, resources)))
        {
            foreach (var prototype in decorations.Prototypes)
            {
                var root = factory.Create(prototype, out var reason);
                if (root is null) throw new InvalidDataException(reason ?? $"Missing decoration prototype '{prototype}'.");
                try { selected.Add(EnbRetailTerrainDecorationSelector.Select(root)); }
                finally { EnbRetailTerrainShadowObjectFactory.Release(root); }
            }
            random.State = factory.CrtThreadSeed;
        }
        IReadOnlyList<EnbW3dRenderMesh> decorationMeshes;
        string mismatch = "";
        try { decorationMeshes = EnbRetailTerrainDecorationMesh.Build(terrain, decorations, selected, out _); }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("Decoration prototypes have different", StringComparison.Ordinal))
        {
            // 0047A2F0 compares each prototype's texture, shader and vertex-material checksum
            // with the first and returns without decoration buffers on a mismatch.
            decorationMeshes = [];
            mismatch = $" (retail prototype mismatch, no decoration buffers: {ex.Message})";
        }
        string water = "no sea";
        if (terrain.HasSea)
            water = EnbRetailTerrainWater.TryAcquireSwatch(prototypes, assets.FileResources, resources, terrain.SeaSwatch, 0, out var mesh, out var why)
                ? $"sea {EnbRetailTerrainWater.Build(terrain, mesh, []).Positions.Count} vertices"
                : $"no sea swatch ({why})";
        return $"patches {surface.Patches.Count} decorations {decorations.Placements.Length}/{decorationMeshes.Count} batches{mismatch} {water}";
    }

    [Fact]
    public void EveryInstalledTerrainPrepares()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var resources = assets.RetailMixResources ?? assets.MixResources!;
        string release = Path.GetDirectoryName(assets.Paths.ClientExecutablePath)!;
        var files = new EnbRetailFileResolver(release, resources);
        var sectors = new SortedSet<uint>();
        var pattern = new Regex(@"^land(\d{4})\.ini$", RegexOptions.IgnoreCase);
        foreach (var location in resources.AllResources)
            if (pattern.Match(Path.GetFileName(location.Name)) is { Success: true } m) sectors.Add(uint.Parse(m.Groups[1].Value));
        foreach (string loose in Directory.EnumerateFiles(assets.Paths.CommonDataDirectory, "land*.ini", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }))
            if (pattern.Match(Path.GetFileName(loose)) is { Success: true } m) sectors.Add(uint.Parse(m.Groups[1].Value));
        string cache = Path.Combine(Path.GetTempPath(), "enbnext-terrain-corpus-" + Guid.NewGuid().ToString("N"));
        var lines = new List<string>();
        var failures = new List<string>();
        var prototypes = EnbW3dRenderResourceCache.ForAssetNamespace(assets);
        try
        {
            foreach (uint sector in sectors)
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    var prepared = EnbRetailTerrainPreparation.Prepare(sector, files, resources, prototypes, 0, 16384, cache);
                    lines.Add($"land{sector:D4} ok width {prepared.Terrain.Width} regenerated {prepared.Regenerated} decorations {(prepared.Decorations is null ? "none" : "yes")} decorationDiagnostic {prepared.DecorationDiagnostic ?? "-"} {watch.ElapsedMilliseconds} ms");
                    if (prepared.DecorationDiagnostic is not null) failures.Add($"land{sector:D4} decorations: {prepared.DecorationDiagnostic}");
                    lines.Add($"land{sector:D4} build {BuildLikeLiveWorldTerrain(prepared, assets, resources, prototypes)}");
                    if (!prepared.Regenerated)
                    {
                        // Cold-cache build of the same prelight (EnbRetailTerrainPreparation.Prepare Build()).
                        var t = prepared.Terrain;
                        Assert.True(files.TryRead($"land{sector:D4}.ini", out byte[] iniBytes, out _, out _));
                        using var ini = new EnbRetailTerrainOwnedIni(iniBytes);
                        byte[] bgr = EnbRetailTerrainColorMap.CreatePrelightPlane(prepared.Color, t.Width, t.FlipColorMap);
                        float spacing = t.HorizontalScale / t.Width;
                        var cold = Stopwatch.StartNew();
                        if (!ini.HasSection("SHADOW0"))
                            EnbRetailTerrainPrelightBuilder.Build(t.Width, t.Heights, spacing, spacing, t.VerticalScale, t.Wrapping, bgr, [], t.OriginalMaximum);
                        else
                        {
                            using var factory = new EnbRetailTerrainShadowObjectFactory(resources, prototypes, 0, EnbRetailCrtRandomSource.Current.State, files);
                            EnbRetailTerrainPrelightBuilder.BuildFromIni(factory, ini, t.Width, t.Heights, spacing, spacing, t.VerticalScale, t.Wrapping, bgr, t.OriginalMaximum, default);
                        }
                        lines.Add($"land{sector:D4} cold prelight shadow {ini.HasSection("SHADOW0")} {cold.ElapsedMilliseconds} ms");
                    }
                }
                catch (EnbRetailTerrainLoader.MissingHeightMapException ex)
                {
                    // 004637C0/004634F0/004691D0: a missing height file yields no terrain.
                    lines.Add($"land{sector:D4} absent (retail no-terrain): {ex.Message}");
                }
                catch (Exception ex)
                {
                    failures.Add($"land{sector:D4}: {ex.GetType().Name}: {ex.Message}");
                    lines.Add($"land{sector:D4} FAIL {ex.GetType().Name}: {ex.Message} {watch.ElapsedMilliseconds} ms");
                }
            }
        }
        finally { try { Directory.Delete(cache, true); } catch (IOException) { } }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-terrain-corpus.txt"),
            [$"sectors {sectors.Count}", .. lines, "failures:", .. failures]);
        Assert.Empty(failures);
    }
}
