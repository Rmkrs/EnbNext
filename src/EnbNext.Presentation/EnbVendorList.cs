using System.Collections.Immutable;

namespace EnbNext.Presentation;

public static class EnbVendorList
{
    public static string StackText(uint? maximum)
    {
        int count=unchecked((int)maximum.GetValueOrDefault(1));
        return count<2?"":FormattableString.Invariant($"{count} per stack");
    }
    // Current client.exe 00BAE854; these captions are binary strings, not INI entries.
    public static readonly ImmutableArray<string> CategoryNames =
        ["Devices", "Weapon", "Shields", "Sensors", "Ejection", "Turrets", "Engines", "Reactors",
         "Controller", "Robots", "Ammo", "Equipable", "Activatable", "Goods", "Beam", "Missile",
         "Projectile", "Counter", "Override"];

    // 00688B90 reads definition +0C (ItemType), independently of the active filter.
    private static uint TypeBit(uint type) => type == uint.MaxValue ? 0 : 1u << (int)type;
    public static uint CategoryMask(IEnumerable<EnbSceneItemSnapshot> stock)
        => stock.Aggregate(0u, (mask, item) => mask | TypeBit(item.ItemType));
    public static ImmutableArray<int> Categories(uint mask)
        => [.. Enumerable.Range(0, 19).Where(type => (mask & (1u << type)) != 0).Take(6)];

    public static ImmutableArray<EnbSceneItemSnapshot> Rows(EnbSceneInventorySnapshot data, int? category, bool compare, bool byName)
    {
        // 00652030 / 00652670: collection 1, 2 (including its secondary ammo layer), 3.
        var owned = compare ? data.Cargo.Concat(data.Equipped).Concat(data.Ammo).Concat(data.Vault)
            .Select(item => item.TemplateId).Where(id => id is not (-1 or -2)).ToHashSet() : null;
        uint mask = category is {} type ? 1u << type : 0;
        var rows = data.Vendor.Where(item => compare ? owned!.Contains(item.TemplateId) : (TypeBit(item.ItemType) & mask) == TypeBit(item.ItemType));
        // 00653450 is stable; its credit comparator treats the high dword as signed.
        return [.. byName ? rows.OrderBy(item => item.Name, StringComparer.Ordinal) : rows.OrderBy(item => unchecked((long)item.Price.GetValueOrDefault()))];
    }
}
