using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // poses every resizable retail UI frame across its authored input range
public sealed class EnbInstalledUiPoseCorpusTests
{
    [Fact]
    public void EveryResizableRetailUiFramePosesAcrossItsAuthoredRange()
    {
        // These frames throw only when an authored animation or skin cannot be
        // sampled/deformed; every clamped frame of the installed resources is posed here.
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var session = EnbRenderSession.Of(assets);
        var failures = new List<string>();
        void Run(Action action, string name)
        {
            try { action(); }
            catch (Exception ex) { if (failures.Count < 200) failures.Add(name + ": " + ex.GetType().Name + " " + ex.Message); }
        }
        // Horizontal slider (EnbHorizontalSliderResources.Sample) and vertical scrollbar thumb.
        for (double length = 0; length <= 2048; length += .5)
        {
            double l = length;
            Run(() => session.HorizontalSlider.Sample(l), $"slider {l}");
        }
        var scrollbar = new EnbVerticalScrollbarVisual(session.VerticalScrollbar);
        for (double height = 0; height <= 2048; height += .5)
        {
            double h = height;
            Run(() => scrollbar.Resize(h), $"scrollbar {h}");
        }
        // Every tooltip/dialog definition (dynawin.ini) with its resize frames.
        string ini = assets.Paths.ClientIniDirectory;
        var fonts = EnbInterfaceLayoutCatalog.Load(Path.Combine(ini, "fc_default.ini"));
        var strings = EnbRetailIniDocument.Load(Path.Combine(ini, "tooltip.ini"));
        var windows = EnbRetailIniDocument.Load(Path.Combine(ini, "dynawin.ini"));
        int frames = 0;
        foreach (string name in windows.EnumerateSections().Select(s => s.Key).Concat(strings.EnumerateSections().Select(s => s.Key)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            EnbRetailScannerTooltipDefinition? definition = null;
            Run(() => definition = EnbRetailScannerTooltipDefinition.Parse(fonts, strings, windows, name), "parse " + name);
            if (definition is null) continue;
            EnbAuthoredTooltipFrame? frame = null;
            Run(() => frame = EnbAuthoredTooltipFrame.Load(assets, definition), "load " + name);
            if (frame is null) continue;
            frames++;
            Run(() => session.Frontend.DialogFrame(name), "dialog frame " + name);
            for (float w = definition.MinWidth; w <= definition.MaxWidth; w++)
            {
                float x = w;
                Run(() => frame.Geometry(x, definition.MinHeight), $"{name} width {x}");
            }
            for (float h = definition.MinHeight; h <= definition.MaxHeight; h++)
            {
                float y = h;
                Run(() => frame.Geometry(definition.MinWidth, y), $"{name} height {y}");
                Run(() => frame.Geometry(definition.MaxWidth, y, true), $"{name} compact height {y}");
            }
        }
        // Chat window layouts (tsi05d/e/f size and split animations).
        for (int mode = 0; mode < 3; mode++)
        {
            EnbRetailChatLayout? layout = null;
            int m = mode;
            Run(() => layout = EnbRetailChatLayout.Load(assets, m, 0), "chat layout " + m);
            if (layout is null) continue;
            var pose = new EnbRetailChatLayoutPose(layout);
            for (int size = 0; size <= layout.SizeFrames; size++)
                for (int split = 0; split <= (mode == 0 ? 0 : layout.SplitFrames); split += mode == 0 ? 1 : Math.Max(1, layout.SplitFrames / 64))
                {
                    int s = size, p = split;
                    Run(() => pose.EvaluatePose(s, mode == 0 ? null : p), $"chat {m} {s}/{p}");
                }
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-ui-pose-corpus.txt"), new[] { $"tooltip frames {frames}" }.Concat(failures));
        Assert.True(frames > 0);
        Assert.Empty(failures);
    }
    private static readonly string[] FixedUiModels =
    [
        "buy_ship04.w3d", "jweb01.w3d", "map_from.w3d", "map_load00.w3d", "map_select.w3d", "opt02a.w3d", "opt04a.w3d", "psi09a.w3d",
        "slider_hor.w3d", "slider_lt.w3d", "slider_rt.w3d", "slider_vert.w3d", "trade00a.w3d", "tsi01a.w3d", "tsi01a_circle.w3d",
        "tsi01a_right.w3d", "tsi01a_short.w3d", "tsi02a.w3d", "tsi05b.w3d", "tsi05c.w3d", "tsi08a.w3d", "tsi08c.w3d", "tsi14a.w3d",
        "tsi26a.w3d", "tsi29a.w3d", "tsi31a.w3d", "vb_scan.w3d", "vb_land.w3d", "vb_loot.w3d", "vb_group.w3d", "vb_message.w3d",
        "vb_trade.w3d", "vb_tractor.w3d", "vb_dock.w3d", "vb_prospect.w3d", "vb_gate.w3d", "vb_register.w3d", "vb_jumpstart.w3d",
        "vb_follow.w3d", "vb_def.w3d",
    ];

    [Fact]
    public void EveryAuthoredUiModelLoadsAndResolvesItsHierarchy()
    {
        // EnbAuthoredUiAsset.Load returns null and ResolveTransforms throws only for a
        // missing or unevaluable authored hierarchy. The fixed names used by the cockpit,
        // scanner verbs, loot, trade and map panels all load; every installed W3D that loads
        // as a UI asset resolves its hierarchy.
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var failures = new List<string>();
        foreach (string name in FixedUiModels)
            if (EnbAuthoredUiAsset.Load(assets, name) is null) failures.Add("fixed UI model unavailable " + name);
        int loaded = 0;
        foreach (var location in assets.RetailMixResources!.Resources.Where(r => r.Name.EndsWith(".w3d", StringComparison.OrdinalIgnoreCase)))
        {
            EnbAuthoredUiAsset? asset = null;
            try { asset = EnbAuthoredUiAsset.Load(assets, location.Name); }
            catch (Exception ex) { failures.Add($"load {location.Name}: {ex.GetType().Name} {ex.Message}"); continue; }
            if (asset is null) continue;
            loaded++;
            try { EnbAuthoredUiAsset.ResolveTransforms(asset); }
            catch (Exception ex) { failures.Add($"transforms {location.Name}: {ex.GetType().Name} {ex.Message}"); }
        }
        // Loot window opening animation (PSI09A-1) over every frame.
        using (var inventory = new EnbInventoryUi(() => 1))
        {
            const System.Reflection.BindingFlags instance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            typeof(EnbInventoryUi).GetMethod("Load", instance)!.Invoke(inventory, [assets]);
            var animation = (EnbW3dRenderHierarchyAnimation)typeof(EnbInventoryUi).GetField("_lootAnimation", instance)!.GetValue(inventory)!;
            var pose = typeof(EnbInventoryUi).GetMethod("SetLootPose", instance)!;
            for (float frame = 0; frame <= animation.NumFrames; frame += .25f)
            {
                try { pose.Invoke(inventory, [frame]); }
                catch (System.Reflection.TargetInvocationException ex) { failures.Add($"loot frame {frame}: {ex.InnerException?.Message}"); break; }
            }
            var frames = (System.Collections.IDictionary)typeof(EnbInventoryUi).GetField("_frames", instance)!.GetValue(inventory)!;
            foreach (System.Collections.DictionaryEntry entry in frames)
                if (EnbAuthoredUiAsset.Load(assets, ((EnbAuthoredTooltipFrame)entry.Value!).Definition.MeshFile + ".w3d") is null)
                    failures.Add("dynamic window hierarchy " + entry.Key);
        }
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-ui-model-corpus.txt"), new[] { $"loaded {loaded}" }.Concat(failures));
        Assert.True(loaded > 0);
        Assert.Empty(failures);
    }
}
