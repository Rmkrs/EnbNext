using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // runs every installed ring and sphere assembly through its runtime and draw packets
public sealed class EnbInstalledRingSphereRuntimeCorpusTests
{
    [Fact]
    public void EveryInstalledRingAndSphereAssemblyPosesSamplesAndBuildsPackets()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var catalog = assets.PresentationCatalog!;
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(0, 0, -50), Quaternion.Identity, new(800, 600, 1), Math.PI / 3, new(.01f, 100000), out var camera));
        var failures = new List<string>();
        int rings = 0, spheres = 0, packets = 0;
        for (int id = 0; id <= 32767; id++)
        {
            var definition = catalog.ResolveBaseAsset(id);
            if (definition.Kind is not (EnbBaseAssetDefinitionKind.FileName or EnbBaseAssetDefinitionKind.ProceduralWidth or EnbBaseAssetDefinitionKind.NullObject)) continue;
            EnbCurrentRetailRObjEffectAsset asset;
            try { asset = EnbCurrentRetailRObjEffectAsset.Load(assets, id); }
            catch (Exception) { continue; } // factory failures are covered by EnbInstalledBaseFactoryCorpusTests
            var observations = asset.Sequence?.CreateObservations();
            try
            {
                if (asset.Rings is { } ringAssembly)
                {
                    rings++;
                    var runtime = new EnbRetailRingAssemblyRuntime(ringAssembly);
                    ulong serial = 0;
                    for (uint age = 0; age <= 20000; age += 250)
                    {
                        var sample = observations?.Record(age);
                        var before = runtime.BeforeAllocation(++serial, age, Matrix4x4.Identity, 1, true, sample);
                        foreach (var r in before.Rings) r.Candidate("ring", camera, 20);
                        var drawn = before.Rings.Select(r => r.Binding.Ordinal).ToHashSet();
                        var frame = runtime.Prepare(serial, age, 250, Matrix4x4.Identity, 1, true, drawn, sample);
                        foreach (var ring in frame.Rings)
                            for (int level = 0; level <= EnbSphereRingDetailBudgetSemantics.ClampRingLevel(int.MaxValue); level++)
                                if (ring.Values.Sample(level) is { } values)
                                {
                                    EnbRetailRingDrawPacket.Build(ring.Binding.Asset, values, ring.Binding.Key, ring.Binding.Texture, ring.DrawWorld(camera), true, age);
                                    packets++;
                                }
                    }
                }
                if (asset.Spheres is { } sphereAssembly)
                {
                    spheres++;
                    var runtime = new EnbRetailSphereAssemblyRuntime(sphereAssembly);
                    var sphereObservations = asset.Sequence?.CreateObservations();
                    ulong serial = 0;
                    for (uint age = 0; age <= 20000; age += 250)
                    {
                        var sample = sphereObservations?.Record(age);
                        var before = runtime.BeforeAllocation(++serial, age, Matrix4x4.Identity, 1, true, sample);
                        foreach (var s in before.Spheres) s.Candidate("sphere", camera, 20);
                        var drawn = before.Spheres.Select(s => s.Binding.Ordinal).ToHashSet();
                        var frame = runtime.Prepare(serial, age, 250, Matrix4x4.Identity, 1, true, drawn, sample);
                        foreach (var sphere in frame.Spheres)
                            for (int level = 0; level <= EnbSphereRingDetailBudgetSemantics.ClampSphereLevel(int.MaxValue); level++)
                                if (EnbRetailSphereGeometry.ForDetail(level) is { } geometry)
                                {
                                    EnbRetailSphereDrawPacket.Build(sphere.Binding.Asset.Header, sphere.Values, geometry, sphere.Binding.Key, sphere.Binding.Texture, sphere.DrawWorld(camera), true, age);
                                    packets++;
                                }
                    }
                }
            }
            catch (Exception ex) { failures.Add($"BASE-{id}: {ex.GetType().Name}: {ex.Message}"); }
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-ring-sphere-runtime-corpus.txt"),
            new[] { $"rings {rings} spheres {spheres} packets {packets}" }.Concat(failures));
        Assert.True(rings > 0 && spheres > 0);
        Assert.Empty(failures);
    }
}
