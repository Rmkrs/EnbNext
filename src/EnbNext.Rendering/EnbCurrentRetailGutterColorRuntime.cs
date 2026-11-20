using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>
/// Selects the <c>gutter_color.ini</c> section used by current retail for one
/// radar object. This preserves the precedence in
/// BuildGutterColorConfigForObject (client.exe 0x0070B0A0): object types which
/// have no reaction palette are Neutral before attacking/player/relationship
/// state is considered.
/// </summary>
public static class EnbCurrentRetailGutterColorRuntime
{
    // 007BB6E7..007BB7F6: unsigned clock modulo 2000, second color
    // through 1000 inclusive; first color for the rest of the cycle.
    public static EnbRgb AlternatingColor(EnbRgb first, EnbRgb second, long milliseconds)
        => unchecked((uint)milliseconds) % 2000 <= 1000 ? second : first;

    // RadarObjectRenderDataRefresh 007BB570, selected state 2. The clock is
    // global, not restarted when selection changes. Target2 -> Target1 -> Target2.
    public static EnbRgb SelectedColor(EnbRgb first, EnbRgb second, long milliseconds)
    {
        float blend = MathF.Abs(unchecked((uint)milliseconds) % 1000 * .001f - .5f) * 2;
        return new(first.R + (second.R - first.R) * blend,
            first.G + (second.G - first.G) * blend,
            first.B + (second.B - first.B) * blend);
    }

    /// <summary>
    /// Exact negative switch in TryGetNeutralGutterColorConfig
    /// (client.exe 0x0070B450). Only these seven object types continue into
    /// attacking/group/PvP/player/relationship selection; every other type
    /// uses the authored Neutral palette.
    /// </summary>
    public static bool UsesReactionPalette(byte objectType)
        => objectType is 0x00 or 0x01 or 0x02 or 0x0B or 0x0C or 0x1C or 0x21;

    public static string ResolveCategory(
        byte objectType,
        uint relationshipRaw,
        bool isAttacking,
        bool isGroupMember = false,
        bool isPvpAttackable = false)
    {
        if (!UsesReactionPalette(objectType))
            return "Neutral";
        if (isAttacking)
            return "Attacking";
        if (isGroupMember)
            return "Group";
        if (isPvpAttackable)
            return "PVPAttackable";
        if (objectType == 0x01)
            return "Player";

        return relationshipRaw switch
        {
            0 => "KOS",
            1 => "Shun",
            2 => "Friendly",
            3 => "Adore",
            _ => "Neutral",
        };
    }
}
