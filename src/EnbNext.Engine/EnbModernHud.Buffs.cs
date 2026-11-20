using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbModernHud
{
    // Buffs under each unit frame: up to two rows of eight retail buff icons (buffdef.ini assets) with the time left under each.
    public const double BuffIcon = 18, BuffGap = 3, BuffLabel = 10, BuffRow = BuffIcon + BuffLabel, BuffArea = 2 + 2 * BuffRow;
    private const int BuffsPerRow = 8;
    private ImmutableArray<EnbSceneBuffSnapshot> playerBuffs = [], targetBuffs = [];
    private long clientTime;
    private int buffKey;
    private EnbRetailBuffCatalog? buffCatalog;
    private readonly List<(string Name, EnbAuthoredUiAsset? Asset, EnbUiRect Design)> buffArt = [];

    private ImmutableArray<EnbSceneBuffSnapshot> previewBuffs = [];
    /// <summary>Edit mode: two full rows of buffs with timers, so the frames' space can be judged.</summary>
    private ImmutableArray<EnbSceneBuffSnapshot> PreviewBuffs()
    {
        if (!previewBuffs.IsEmpty || buffCatalog is null) return previewBuffs;
        return previewBuffs = [.. buffCatalog.Definitions.Values.OrderBy(d => d.Type, StringComparer.Ordinal).Take(15)
            .Select((d, i) => new EnbSceneBuffSnapshot(i, d.Type, "", false, clientTime + 30_000L + i * 97_000L, []))];
    }

    private void UpdateBuffs(ImmutableArray<EnbSceneBuffSnapshot> self, ImmutableArray<EnbSceneBuffSnapshot> selected, long time)
    {
        playerBuffs = self.IsDefault ? [] : self; targetBuffs = selected.IsDefault ? [] : selected; clientTime = time;
        ObserveBuffSources();
        // Rebuild only when what the rows show changes: which buffs, their stack counts and the displayed time left.
        var hash = new HashCode();
        foreach (var list in (ReadOnlySpan<ImmutableArray<EnbSceneBuffSnapshot>>)[playerBuffs, targetBuffs])
        {
            hash.Add(list.Length);
            foreach (var buff in list)
            {
                if (!buff.IsVisible(time)) continue;
                hash.Add(buff.Slot); hash.Add(buff.Type); hash.Add(buff.Elements.Length);
                if (!buff.IsPermanent) hash.Add(Remaining(buff.RemovalTime - time));
            }
        }
        int key = hash.ToHashCode();
        if (key != buffKey) { buffKey = key; cached = null; cachedTooltip = null; }
    }

    /// <summary>Time left as shown under a buff: whole hours, minutes or seconds.</summary>
    private static (int Value, char Unit) Remaining(long milliseconds)
    {
        long seconds = Math.Max(0, (milliseconds + 999) / 1000);
        return seconds >= 3600 ? ((int)(seconds / 3600), 'h') : seconds >= 60 ? ((int)(seconds / 60), 'm') : ((int)seconds, 's');
    }

    private (EnbAuthoredUiAsset Asset, EnbUiRect Design)? BuffArt(string name)
    {
        foreach (var entry in buffArt) if (entry.Name == name) return entry.Asset is null ? null : (entry.Asset, entry.Design);
        EnbAuthoredUiAsset? asset = null; EnbUiRect design = default;
        try
        {
            asset = EnbAuthoredUiAsset.Load(assets, name + ".w3d");
            if (asset is not null && asset.Resource.Meshes.Count > 0)
                design = (asset.Resource.Meshes.Any(m => m.Name == "DOCK") ? EnbCockpitUi.MeshBounds(asset, "DOCK")
                    : asset.Resource.Meshes.Select(m => EnbCockpitUi.MeshBounds(asset, m.Name)).Aggregate((a, b) => a.Union(b))).Translate(new(-320, -240));
            else asset = null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { asset = null; } // That buff shows no icon.
        buffArt.Add((name, asset, design));
        return asset is null ? null : (asset, design);
    }

    /// <summary>The hovered buff's retail tooltip (name and contributing elements).</summary>
    private string? BuffTip(string key)
    {
        // key: buff/p/<slot> (player) or buff/t/<slot> (target)
        var list = key[5] == 't' ? targetBuffs : playerBuffs;
        if (!int.TryParse(key.AsSpan(7), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int slot) || buffCatalog is null) return null;
        foreach (var buff in list)
            if (buff.Slot == slot && buffCatalog.Definitions.TryGetValue(buff.Type, out var definition)) return buff.Tooltip(definition);
        return null;
    }
}
