namespace EnbNext.Presentation;

/// <summary>
/// Next "always show labels" categories. Built from the object types retail's gutter already distinguishes
/// (GutterRadarGetIconAssetNameForObjectType 0x007B6A00) and its reaction palette (KOS or attacking is hostile).
/// </summary>
public static class EnbObjectLabels
{
    public const int Hostiles = 0, Npcs = 1, Players = 2, Stations = 3, Planets = 4, Navigation = 5, Resources = 6, Loot = 7, Other = 8, Count = 9;
    public const int All = (1 << Count) - 1;
    public static readonly string[] Names =
        ["Hostile NPCs", "Other NPCs", "Players", "Stations, starbases and gates", "Planets", "Navigation points", "Asteroids and resources", "Loot and items", "Everything else"];

    public static int Classify(byte objectType, uint relationshipRaw, bool isAttacking, bool isNavigation) => objectType switch
    {
        0x00 or 0x02 => isAttacking || relationshipRaw == 0 ? Hostiles : Npcs,
        0x01 => Players,
        0x0B or 0x0C or 0x18 => Stations,
        0x03 => Planets,
        0x22 or 0x26 => Resources,
        0x04 => Loot,
        _ => isNavigation ? Navigation : Other,
    };

    public static bool Shows(int mask, int kind) => (mask & (1 << kind)) != 0;
}
