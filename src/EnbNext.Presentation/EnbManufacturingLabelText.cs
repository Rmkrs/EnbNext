using System.Globalization;

namespace EnbNext.Presentation;

// Retail manufacturing terminal texts (client.exe
// 249f852496f341c00361be95b66e7c0becc2c39e7cba026f9227e38b602651c4).
// 005B4B10 (open) and 005B5320 (per update) write Quality_Range_tsm01a and
// Expected_Quality_tsm01a with different formats; 005B6060 supplies the
// "Unable to create" reason.
public static class EnbManufacturingLabelText
{
    public const string Unavailable = "N/A"; // 00B818D8
    private const float Sentinel = -1.0f;    // 00AF2D14

    // Range: min < 0 -> 0, max > 3 -> 3, then a pair only when min < max;
    // otherwise (equal, inverted or NaN) the single value is min, replaced by
    // max unless min <= max. Open: "%2.0f%%-%2.0f%%" / "%2.0f"; update:
    // "%2.0f%% to %2.0f%%" / "%2.0f%%".
    public static string Range(float minimum, float maximum, bool opening)
    {
        if (minimum == Sentinel || maximum == Sentinel) return Unavailable;
        if (minimum < 0.0f) minimum = 0.0f;
        if (maximum > 3.0f) maximum = 3.0f;
        if (minimum <= maximum)
        {
            if (minimum != maximum)
                return opening ? $"{Fixed(minimum)}%-{Fixed(maximum)}%" : $"{Fixed(minimum)}% to {Fixed(maximum)}%";
        }
        else minimum = maximum;
        return opening ? Fixed(minimum) : Fixed(minimum) + "%";
    }

    // Expected: 0 <= value else 0 (NaN included), > 3 -> 3. Open "%2.0f",
    // update "%2.0f%%".
    public static string Expected(float expected, bool opening)
    {
        if (expected == Sentinel) return Unavailable;
        if (!(0.0f <= expected)) expected = 0.0f;
        else if (expected > 3.0f) expected = 3.0f;
        return opening ? Fixed(expected) : Fixed(expected) + "%";
    }

    // Analysis Chance_tsm02a / Critical_tsm02a, "%1.0f%%" (00B79AB0). The open
    // writer 00544D80 stores value * 100 in a float first; the per-update
    // writer 00545190 passes the x87 product, exact in a double.
    public static string Probability(float probability, bool opening)
        => Percent(opening ? (double)(probability * 100.0f) : (double)probability * 100.0);

    public static string Percent(double value)
        => Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";

    // 005B6060: empty for 0 and 0x0D..0x11, the listed reasons, otherwise
    // "Unexpected error.".
    public static string FailureReason(int validity) => validity switch
    {
        0 or (>= 0x0D and <= 0x11) => "",
        4 => "Missing sufficient credits",
        5 => "Missing required component(s)",
        6 => "Missing sufficient cargo room",
        7 => "Missing required skill level",
        8 => "Too difficult",
        0x0C => "Unique item already owned",
        _ => "Unexpected error.",
    };

    // 005EE7F0, the refinery's table (same empty and default cases).
    public static string RefineryFailureReason(int validity) => validity switch
    {
        0 or (>= 0x0D and <= 0x11) => "",
        4 => "Insufficient credits to refine item.",
        5 => "Missing required component(s)",
        6 => "More empty cargo space required to refine item.",
        7 => "Not qualified to refine item.",
        8 => "Item too difficult to refine.",
        0x0C => "You cannot refine an unique resource you already possess.",
        _ => "Unexpected error.",
    };

    // 005B5320 and 005EDFD0: "Unable to create " + item + ". " + reason + ".".
    public static string UnableToCreate(string item, string reason) => "Unable to create " + item + ". " + reason + ".";

    // "%2.0f" of (double)(value * 100.0f): the x87 product of two floats is
    // exact in a double. Finite values only reach here after the clamps.
    private static string Fixed(float value)
        => Math.Round((double)value * 100.0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture).PadLeft(2);
}
