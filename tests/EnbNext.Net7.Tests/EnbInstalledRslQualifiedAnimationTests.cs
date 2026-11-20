using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbInstalledRslQualifiedAnimationTests
{
    // 00459D50 uses an ANIM that contains '.' verbatim as the asset-manager key,
    // so its hierarchy prefix need not be the RenderObj hierarchy. This receipt
    // lists every installed authored ANIM of that form.
    [Fact]
    public void ListsInstalledHierarchyQualifiedAnimTokens()
    {
        if (EnbInstalledAssetLocator.TryLocate() is not {} paths) return;
        var states = EnbRetailAssetBundle.Load(paths).RenderStates;
        Assert.NotNull(states);
        var lines = new List<string>();
        for (int id = 0; id < states!.LibraryCount; id++)
        {
            Assert.True(states.TryGetLibrary(id, out var library));
            foreach (var state in library.States)
                if (state.Animation is { } anim && anim.Contains('.'))
                    lines.Add($"{id}\t{state.StateId}\t{state.File}\t{state.Object}\t{anim}\t{state.Suffix}");
        }
        var ids = lines.Select(l => int.Parse(l.Split('\t')[0])).ToHashSet();
        foreach (int id in ids)
        {
            states.TryGetLibrary(id, out var library);
            foreach (var st in library.States)
                lines.Add($"lib {id} state {st.StateId} {st.TypeToken} file={st.File} obj={st.Object} anim={st.Animation} suffix={st.Suffix} next={st.NextStateId} link={st.LinkStateId}");
        }
        var resolver = EnbRetailAssetBundle.Load(paths).BaseAssetResolver!;
        for (int selector = 0; selector <= 32767; selector++)
        {
            EnbBaseAssetResolution r;
            try { r = resolver.Resolve(selector); } catch { continue; }
            if (r.Definition.RslId is int rsl && ids.Contains(rsl))
                lines.Add($"base {selector} rsl {rsl} status {r.Status} file={r.Definition.File} name={r.Definition.Name}");
        }
        var mix = EnbRetailAssetBundle.Load(paths).MixResources!;
        foreach (var location in mix.Resources.Where(r => r.Name.Contains("mwrk01a", StringComparison.OrdinalIgnoreCase) || r.Name.Equals("BG.W3D", StringComparison.OrdinalIgnoreCase)))
        {
            if (!mix.TryReadResource(location, out byte[] bytes) || !EnbW3dModel.TryDecode(bytes, out var model))
            {
                lines.Add($"w3d {location.Name} undecodable size {bytes?.Length} archive {location.ArchiveName}");
                for (int o = 0; bytes is not null && o + 8 <= bytes.Length;)
                {
                    uint type = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(o));
                    uint size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(o + 4));
                    lines.Add($"  chunk @{o} type 0x{type:X} size 0x{size:X}");
                    o += 8 + (int)(size & 0x7fffffff);
                    if (lines.Count > 400) break;
                }
                continue;
            }
            foreach (var h in model.Hierarchies)
                lines.Add($"w3d {location.Name} hierarchy {h.Name} pivots {h.Pivots.Count} [{string.Join(",", h.Pivots.Select(p => p.Name))}]");
            foreach (var a in model.Animations)
                lines.Add($"w3d {location.Name} anim {a.Name} hierarchy {a.HierarchyName} frames {a.NumFrames} channels {a.Channels.Count} maxPivot {(a.Channels.Count == 0 ? -1 : a.Channels.Max(c => (int)c.PivotIndex))} bits {a.BitChannels.Count}");
            lines.Add($"w3d {location.Name} meshes {model.Meshes.Count} hlods {model.HierarchicalLods.Count} [{string.Join(",", model.HierarchicalLods.Select(l => l.Name + ":" + l.HierarchyName))}]");
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-rsl-qualified-anim.txt"),
            lines.Prepend($"libraries {states.LibraryCount} states {states.StateCount} qualified {lines.Count}"));
    }
}
