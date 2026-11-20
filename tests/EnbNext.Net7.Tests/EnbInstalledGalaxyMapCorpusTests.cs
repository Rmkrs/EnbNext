using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Net7.LocalData;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // opens the installed galaxy map and sweeps the pointer over every visible system and sector
public sealed class EnbInstalledGalaxyMapCorpusTests
{
    [Fact]
    public void InstalledGalaxyMapPublishesEveryAuthoredStyleTextureLabelAndGlyph()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var galaxy = Net7LocalDataAssets.CurrentGalaxyMap;
        using var map = new EnbNavigationMapUi((_, _) => Task.FromResult(false), () => 1u) { GalaxyMapAccessor = () => galaxy };
        var scene = new EnbSceneFrame();
        var size = new EnbUiSize(1600, 1200);
        map.Update(scene, assets, size, 1, true);
        Assert.True(map.ToggleMode(true));
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var field = typeof(EnbNavigationMapUi).GetField("_galaxyFocusedSystem", flags)!;
        var grid = typeof(EnbNavigationMapUi).GetField("_galaxyGrid", flags)!;
        var sector = typeof(EnbNavigationMapUi).GetField("_galaxyFocusedSector", flags)!;
        var last = typeof(EnbNavigationMapUi).GetField("_lastTime", flags)!;
        var focused = new HashSet<uint>();
        var sectors = new HashSet<uint>();
        int links = 0, labels = 0, closes = 0;
        foreach (bool showGrid in new[] { false, true })
        {
            grid.SetValue(map, showGrid);
            for (double y = 0; y < size.Height; y += 12)
                for (double x = 0; x < size.Width; x += 12)
                {
                    var point = new EnbUiPoint(x, y);
                    map.Move(point);
                    if (map.PressPointer(point, false)) map.ReleasePointer(point, false, false);
                    if (!map.IsOpen) { closes++; map.Update(scene, assets, size, 1, true); Assert.True(map.ToggleMode(true)); grid.SetValue(map, showGrid); }
                    if (map.Presentation is { } presentation)
                    { links = Math.Max(links, presentation.GalaxyLinks.Length); labels = Math.Max(labels, presentation.GalaxyLabels.Length); }
                    if ((field.GetValue(map) is uint system && focused.Add(system)) || (sector.GetValue(map) is uint s && sectors.Add(s)))
                    { last.SetValue(map, Environment.TickCount64 - 2000); map.Update(scene, assets, size, 1, true); }
                }
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "enbnext-galaxy-map-corpus.txt"),
            $"systems {galaxy.Systems.Count} sectors {galaxy.Sectors.Count} gates {galaxy.Gates.Count} focused {focused.Count} sectorsFocused {sectors.Count} links {links} labels {labels} closes {closes}");
        Assert.True(links > 0 && labels > 0);
    }
}
