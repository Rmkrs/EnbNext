using EnbNext.Net7.Social;

namespace EnbNext.Presentation;

public enum EnbGroupFinderAction { Search, CloseList, Option, Leave, Disband, Target }
public sealed record EnbGroupFinderRequest(uint Character, ulong ShipOwner, EnbGroupFinderAction Action,
    uint Option = 0, bool Enabled = false, uint CandidateId = 0, long Revision = 0);

public static class EnbGroupFinder
{
    public const int VisibleRows = 8;
    public static bool AllowsOption(bool grouped, bool leader, uint option) => option switch
    {
        0 => !grouped, 1 => true, 3 or 4 => grouped && leader, 5 => !grouped || leader, _ => false
    };

    // 00599F30: stable arrival-order ties; the numeric comparator is signed.
    public static Net7GroupFinderCandidate[] Sort(IEnumerable<Net7GroupFinderCandidate> rows, int column)
        => (column switch
        {
            0 => rows.OrderBy(r => r.Name, StringComparer.Ordinal),
            1 => rows.OrderBy(r => r.CurrentClassLabel, StringComparer.Ordinal)
                .ThenBy(r => unchecked((int)r.NumericField)).ThenBy(r => r.Name, StringComparer.Ordinal),
            _ => rows.OrderBy(r => unchecked((int)r.NumericField))
                .ThenBy(r => r.CurrentClassLabel, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal)
        }).ToArray();
}
