using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // decodes dazzles, HAnim headers and special-material cubes of every installed W3D
public sealed class EnbInstalledAuxDecoderCorpusTests
{
    private delegate bool PrototypeDecode(ReadOnlySpan<byte> bytes, out EnbW3dModel model);

    [Fact]
    public void EveryInstalledDazzleHAnimHeaderAndCubeDecodes()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var dazzleIni = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "Dazzle.ini"));
        string release = Path.GetDirectoryName(assets.Paths.ClientExecutablePath)!;
        var lines = new List<string>();
        var failures = new List<string>();
        var selectors = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        int rings = 0, spheres = 0, specials = 0, undecoded = 0, files = 0, dazzles = 0, hanimFiles = 0, headers = 0, cubes = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cubeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in assets.RetailMixResources!.AllResources)
        {
            if (!location.Name.EndsWith(".w3d", StringComparison.OrdinalIgnoreCase) || !seen.Add(location.Name)) continue;
            if (!assets.RetailMixResources.TryReadResource(location, out byte[] bytes)) continue;
            files++;
            try
            {
                foreach (var dazzle in EnbW3dDazzle.Decode(bytes))
                { dazzles++; EnbDazzleDefinition.Resolve(dazzleIni, dazzle.Type); }
            }
            catch (Exception ex) { failures.Add($"dazzle {location.Name}: {ex.Message}"); }
            bool hasAnim = false;
            for (int o = 0; o + 8 <= bytes.Length;)
            {
                uint type = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(o));
                uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(o + 4)) & 0x7fffffff;
                if (type is 0x200 or 0x280 or 0x2C0) hasAnim = true;
                if (size > bytes.Length - o - 8) break;
                o += 8 + (int)size;
            }
            if (hasAnim)
            {
                hanimFiles++;
                if (!EnbW3dHAnimHeaderProbe.TryInspect(bytes, out var found, out string? why)) failures.Add($"hanim-header {location.Name}: {why}");
                else headers += found.Count;
            }
            EnbW3dModel? model = null;
            if (!EnbW3dModel.TryDecode(bytes, out var ordinary))
            {
                var m = typeof(EnbW3dModel).GetMethod("TryDecodePrototypeContainer", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
                var decode = m.CreateDelegate<PrototypeDecode>();
                if (decode(bytes, out var container)) model = container; else undecoded++;
            }
            else model = ordinary;
            if (model is not null) specials += model.SpecialMaterials.Count;
            if (model is not null && (model.Rings.Count != 0 || model.Spheres.Count != 0))
            {
                foreach (var ring in model.Rings)
                {
                    rings++;
                    try
                    {
                        if (!assets.W3dFiles.TryResolve(location.Name, ring.Header.Name, out var source, out string? why, out _)) failures.Add($"ring resolve {location.Name}/{ring.Header.Name}: {why}");
                        else EnbRetailRingAttachment.LoadFromSource(assets, source);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { failures.Add($"ring {location.Name}/{ring.Header.Name}: {ex.Message}"); }
                }
                foreach (var sphere in model.Spheres)
                {
                    spheres++;
                    try
                    {
                        if (!assets.W3dFiles.TryResolve(location.Name, sphere.Header.Name, out var source, out string? why, out _)) failures.Add($"sphere resolve {location.Name}/{sphere.Header.Name}: {why}");
                        else EnbRetailSphereAttachment.LoadFromSource(assets, source);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { failures.Add($"sphere {location.Name}/{sphere.Header.Name}: {ex.Message}"); }
                }
            }
            if (model is not null)
                foreach (var special in model.SpecialMaterials)
                    foreach (var member in special.Members)
                    {
                        if (member.Resource is { } any) selectors.Add($"{any.Selector}:{any.Texture}");
                        if (member.Resource is { Selector: 5 } resource && cubeNames.Add(resource.Texture))
                        {
                            cubes++;
                            bool ok = EnbRetailCubeTexture.TryLoad(release, resource.Texture, out var cube);
                            lines.Add($"cube {location.Name} '{resource.Texture}' exists {File.Exists(Path.GetFullPath(resource.Texture, release))} decoded {ok}");
                            if (!ok) failures.Add($"cube {location.Name} '{resource.Texture}'");
                        }
                    }
        }
        int looseB00 = 0;
        foreach (string loose in Directory.EnumerateFiles(assets.Paths.RootDirectory, "*.w3d", SearchOption.AllDirectories))
        {
            lines.Add("loose " + loose);
            byte[] b = File.ReadAllBytes(loose);
            if (b.Length >= 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b) == 0xb00) looseB00++;
        }
        lines.Add($"looseB00 {looseB00}");
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-aux-decoder-corpus.txt"),
            [$"files {files} dazzles {dazzles} hanimFiles {hanimFiles} headers {headers} cubes {cubes} undecoded {undecoded} specials {specials} rings {rings} spheres {spheres} release {release}", .. lines, "selectors:", .. selectors, "failures:", .. failures]);
        Assert.Empty(failures);
    }
}
