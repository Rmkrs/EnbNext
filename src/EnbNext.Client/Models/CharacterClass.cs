namespace EnbNext.Client.Models;

/// <summary>Earth & Beyond race/class labels used only for presentation.</summary>
public static class CharacterClass
{
    // 00BA8A08 class names; character select uses the same words in "Class: X" (00B7B934).
    private static readonly string[] ClassTable =
    {
        "Enforcer", "Tradesman", "Scout",
        "Defender", "Seeker", "Explorer",
        "Warrior", "Privateer", "Sentinel",
    };

    public static string RaceName(uint race) => race switch
    {
        0 => "Terran",
        1 => "Jenquai",
        2 => "Progen",
        // Safety rule: retail 0055F140 indexes its race/class label tables without a bound;
        // an out-of-range value reads past them, so no label text is produced.
        _ => string.Empty,
    };

    public static string ClassName(uint race, uint profession)
    {
        return TryGetClassIndex(race, profession, out int index)
            ? ClassTable[index]
            : string.Empty;
    }

    private static bool TryGetClassIndex(uint race, uint profession, out int index)
    {
        if (race > 2 || profession > 2)
        {
            index = -1;
            return false;
        }

        index = checked((int)(race * 3 + profession));
        return true;
    }
}
