using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // draws every authored dialog definition, the quit panel and every map marker glyph
public sealed class EnbInstalledDialogMarkerCorpusTests
{
    [Fact]
    public void EveryReachableDialogQuitPanelAndMapMarkerLoadsFromInstalledData()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var palette = EnbFontEffectPalette.Load(assets.Paths.ClientIniDirectory);
        var failures = new List<string>();
        void Run(Action action, string name)
        {
            try { action(); }
            catch (Exception ex) { if (failures.Count < 200) failures.Add(name + ": " + ex.GetType().Name + " " + ex.Message); }
        }
        // EnbConfirmationState definitions: the fixed OK/YES_NO and every dialogbox.ini section (ShowPrompt admits only those).
        var dialogs = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "dialogbox.ini"));
        var definitions = dialogs.SectionNames.Concat(["OK", "YES_NO"]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        EnbUiSize[] sizes = [new(640, 480), new(1024, 768), new(1920, 1080), new(2560, 1440)];
        string text = string.Join('\n', Enumerable.Range(0, 20).Select(i => "Authored confirmation line " + i + " with enough words to wrap twice over the dialog width"));
        int drawn = 0;
        using (var panel = new EnbConfirmationPanel(assets, palette))
            foreach (string definition in definitions)
                foreach (bool notice in new[] { false, true })
                    foreach (float? wrap in new float?[] { null, 280, 320 })
                        foreach (var size in sizes)
                            foreach (double scaling in new[] { 1d, 1.5 })
                            {
                                var state = new EnbConfirmationState(); state.Show(text, notice, definition, wrap);
                                Run(() => { if (panel.DrawFrame(state, size, scaling).Count > 0) drawn++; }, $"dialog {definition} {notice} {wrap} {size} {scaling}");
                            }
        // EnbQuitPanel: both layouts, flight and station, at every size.
        foreach (bool editing in new[] { false, true })
            Run(() =>
            {
                using var quit = new EnbQuitPanel(assets, palette, editing);
                foreach (var size in sizes) foreach (bool station in new[] { false, true }) quit.DrawFrame(size, 1, station);
            }, "quit " + editing);
        // Map marker glyphs for every object type, the unknown marker and the player marker.
        var icons = Enumerable.Range(0, 256).Select(t => EnbNavigationMapProjection.Icon((byte)t, false))
            .Append(EnbNavigationMapProjection.Icon(0, true)).Append("m_pc.w3d").Distinct().ToArray();
        // 007BF4C0 names m_misile/m_mine, which the installed data does not contain: 007BC320 leaves the icon RenderObj
        // null and 007BEAD0 then calls through it, so the map contains that object locally (EnbNavigationMapUi._missingGlyphs).
        string[] absent = ["m_misile.w3d", "m_mine.w3d"];
        foreach (string icon in icons.Except(absent)) Run(() => EnbMapGlyph.Load(assets, icon), "glyph " + icon);
        foreach (string icon in absent) Assert.Throws<InvalidDataException>(() => EnbMapGlyph.Load(assets, icon));
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-dialog-marker-corpus.txt"),
            new[] { $"definitions {definitions.Length} drawn {drawn} icons {icons.Length}" }.Concat(failures));
        Assert.Empty(failures);
    }
}
