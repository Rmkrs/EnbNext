using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // samples every pivot of every frame of every installed W3D hierarchy animation
public sealed class EnbInstalledHAnimSampleCorpusTests
{
    [Fact]
    public void EveryInstalledHierarchyAnimationSamplesEveryPivotAtEveryFrame()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var failures = new List<string>();
        var compileFailures = new List<string>();
        int animations = 0; long samples = 0;
        var foreignHierarchy = new List<string>();
        var duplicateRegistry = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in assets.RetailMixResources!.AllResources)
        {
            if (!location.Name.EndsWith(".w3d", StringComparison.OrdinalIgnoreCase) || !seen.Add(location.Name)) continue;
            string objectName = Path.GetFileNameWithoutExtension(location.Name);
            if (!assets.W3dFiles.TryResolve(location.Name, objectName, out var resolved, out _, out bool opened) || !opened) continue;
            if (!EnbW3dRenderResourceCompiler.TryCompile(resolved, out var resource, out string? compileWhy))
            { compileFailures.Add($"{location.Name}: {compileWhy}"); continue; }
            foreach (var duplicate in resource.Animations.GroupBy(a => a.HierarchyName + "." + a.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                duplicateRegistry.Add($"{location.Name} {duplicate.Key} x{duplicate.Count()}");
            foreach (var animation in resource.Animations)
            {
                animations++;
                // 00989670/00989BC0 apply an HAnim to the owner's HTree by pivot index
                // without a hierarchy-name test; record every in-file HAnim whose
                // hierarchy is not one of the file's own trees.
                if (resource.Hierarchies.Count != 0 && !resource.Hierarchies.Any(h => h.Name.Equals(animation.HierarchyName, StringComparison.OrdinalIgnoreCase)))
                    foreignHierarchy.Add($"{location.Name} {animation.Name} -> {animation.HierarchyName} (file: {string.Join(",", resource.Hierarchies.Select(h => h.Name))})");
                int pivots = 1 + Math.Max(
                    animation.Channels.Count == 0 ? 0 : animation.Channels.Max(c => (int)c.SourcePivotIndex),
                    animation.BitChannels.Count == 0 ? 0 : animation.BitChannels.Max(c => (int)c.SourcePivotIndex));
                for (int frame = 0; frame < Math.Max(1, (int)animation.NumFrames); frame++)
                    for (int pivot = 0; pivot < pivots; pivot++)
                    {
                        samples++;
                        if (!EnbW3dAnimationSampler.TrySamplePivot(animation, pivot, frame, out _, out string? why))
                        { failures.Add($"{location.Name} {animation.Name} pivot {pivot} frame {frame}: {why}"); goto nextAnimation; }
                    }
                nextAnimation:;
            }
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-hanim-sample-corpus.txt"), [$"animations {animations} samples {samples}", .. failures, "compile:", .. compileFailures, $"foreign-hierarchy {foreignHierarchy.Count}:", .. foreignHierarchy, $"duplicate-registry-name {duplicateRegistry.Count}:", .. duplicateRegistry]);
        Assert.True(animations > 1000, $"animations {animations}");
        Assert.Empty(failures);
        Assert.Empty(compileFailures);
    }
}
