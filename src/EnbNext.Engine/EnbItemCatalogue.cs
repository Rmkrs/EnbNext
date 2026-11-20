using EnbNext.Net7.Gameplay;
using EnbNext.Net7.LocalData;
using EnbNext.Presentation;

namespace EnbNext.Engine;

/// <summary>
/// Next item catalogue (retail has none): every item the game knows, from the Net-7
/// launcher's cdata.dat (the item definitions the proxy hands the client; retail 0x0025 payloads), each as the item
/// the inventory shows for that definition (no instance values). Read once, in the background, when first needed.
/// </summary>
internal sealed class EnbItemCatalogue
{
    /// <summary>The items in name order, and for each the lower-case text a search looks in (name, type, specials,
    /// description).</summary>
    internal EnbSceneItemSnapshot[] Items { get; }
    internal string[] SearchText { get; }

    private EnbItemCatalogue(EnbSceneItemSnapshot[] items, string[] search) { Items = items; SearchText = search; }

    private static Task<EnbItemCatalogue>? _loading;
    private static readonly object Sync = new();

    /// <summary>The catalogue, read on first use (it stays read for the session).</summary>
    internal static Task<EnbItemCatalogue> Load()
    {
        lock (Sync) return _loading ??= Task.Run(Read);
    }

    private static EnbItemCatalogue Read()
    {
        var cdata = Net7LocalDataAssets.CurrentCData;
        var items = new List<EnbSceneItemSnapshot>(cdata.RecordCount);
        foreach (uint id in cdata.RecordIds)
        {
            if (!cdata.TryGetRecord(id, out var record) || record is null) continue;
            Net7ItemTemplate template;
            // A record the item codec cannot read is not an item: it is left out of the catalogue.
            try { template = Net7ItemTemplateWire.Decode(record.Payload); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException) { continue; }
            string name = template.Name.EffectiveText.Trim();
            if (name.Length == 0) continue;
            items.Add(EnbSceneInventorySnapshotBuilder.CatalogueItem(template));
        }
        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) is var n and not 0 ? n : a.Level.CompareTo(b.Level));
        var search = new string[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var text = new System.Text.StringBuilder(item.Name).Append('\n').Append(EnbItemInformation.TypeName(item.Subcategory));
            foreach (var special in EnbItemInformation.Specials(item)) text.Append('\n').Append(special.Text);
            text.Append('\n').Append(item.Description);
            search[i] = text.ToString().ToLowerInvariant();
        }
        return new(items.ToArray(), search);
    }
}
