using System.Collections.Immutable;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>A group member as the Modern UI group frame shows it: the retail row's name, whether the member asked for
/// help (the retail Assist button's flash) and the symbol the member sent (symbol chat), if any.</summary>
internal readonly record struct EnbModernMember(string Name, uint GameId, long Identity, bool Attention,
    EnbAuthoredUiAsset? Symbol = null, EnbUiRect SymbolDesign = default, long SymbolSince = 0, bool SymbolContext = false, int? Reactor = null);

/// <summary>
/// Next Modern UI group frame: one frame per other member (the name on its own line over hull | shield, or with the panel
/// option beside them in one row; the cockpit gauges as on the target frame), with no buttons, where the retail group panel stands and stacked upward like it. A click targets the member; a right-click opens the member's menu (assist, ask for help,
/// symbols, remove), and the group's own commands (formation, ask the group for help, leave) are in the player frame's
/// menu. A member out of range has its name dimmed and empty bars; one asking for help flashes.
/// </summary>
internal sealed partial class EnbModernHud
{
    /// <summary>Rows the frame holds (a group has six players; the player is not listed).</summary>
    private const int MaxMembers = 6;
    private const int MemberGauge = 5;
    private const double MemberWidth = 270, MemberBox = TargetBox, MemberName = 100, MemberGap = 4;
    // The default layout: a taller frame with the name on its own line over the bars.
    private const double MemberWidthTall = 200, MemberTall = Plate + Bar + 9;
    /// <summary>The edit mode panel option that puts the name beside the bars.</summary>
    internal const string NameBesideOption = "ModernGroup/NameBeside";
    private bool memberNameBeside;
    private ImmutableArray<EnbModernMember> members = [];
    private readonly IEnbVitalsFrame?[] memberVitals = new IEnbVitalsFrame?[MaxMembers];
    private readonly bool[] memberPresent = new bool[MaxMembers];
    private readonly long[] memberIdentity = new long[MaxMembers];
    private readonly EnbUiRect[] memberRows = new EnbUiRect[MaxMembers];
    private int memberRowCount;
    private bool formationReady, memberAnimating;
    private EnbUiRect groupDesign;
    public event Action<int>? MemberPressed;
    public event Action<int, EnbUiPoint>? MemberMenuRequested;
    public event Action<int>? MemberSymbolPressed;

    /// <summary>The retail group panel's first row (screen space, before its placement): the Modern UI rows stand on
    /// it, right-aligned, and stack upward as the retail rows do.</summary>
    internal Func<EnbUiRect?>? RetailGroupAnchor { get; set; }
    private (double Right, double Bottom) GroupAnchor()
        => RetailGroupAnchor?.Invoke() is { Width: > 0 } r ? (Math.Round(r.Right / zoom), Math.Round(r.Bottom / zoom)) : (size.Width - GutterX, Math.Round(size.Height * .64));

    /// <summary>The group frame's extent before its placement (layout editing).</summary>
    public EnbUiRect GroupBounds => size.Width > 0 && groupDesign.Width > 0 ? Up(groupDesign) : default;
    /// <summary>A member row in screen space (placement applied), for the multibox shortcuts beside it.</summary>
    internal EnbUiRect? MemberRowBounds(int index) => (uint)index < (uint)memberRowCount ? Apply(EnbUiGroup.ModernGroup, memberRows[index]) : null;

    /// <summary>The members (from the group's retained rows), their vitals from the objects in range, and whether the
    /// leader's formation waits for the player to form up.</summary>
    public void UpdateGroup(ImmutableArray<EnbModernMember> list, IReadOnlyList<IEnbRenderObject> objects, bool formation)
    {
        if (list.IsDefault) list = [];
        if (!list.SequenceEqual(members) || formation != formationReady) { members = list; formationReady = formation; cached = null; cachedTooltip = null; }
        int count = Math.Min(list.Length, MaxMembers);
        for (int i = 0; i < count; i++)
        {
            IEnbVitalsFrame? vitals = null; bool present = false;
            for (int o = 0; o < objects.Count; o++)
                if (objects[o].GameId == list[i].GameId && list[i].GameId != 0) { vitals = objects[o].TargetVitals; present = true; break; }
            if (memberIdentity[i] != list[i].Identity)
            {
                memberIdentity[i] = list[i].Identity;
                gauges[MemberGauge + 2 * i].Reset(MemberHull(vitals)); gauges[MemberGauge + 2 * i + 1].Reset(MemberShield(vitals));
                cached = null;
            }
            if (present != memberPresent[i] || !Equals(vitals, memberVitals[i])) { memberPresent[i] = present; memberVitals[i] = vitals; cached = null; cachedTooltip = null; }
        }
        for (int i = count; i < MaxMembers; i++) { memberIdentity[i] = 0; memberVitals[i] = null; memberPresent[i] = false; }
    }

    private static float MemberHull(IEnbVitalsFrame? v) => v is { MaximumHullPoints: >= 1 } ? Math.Clamp((v.HullPoints ?? 0) / v.MaximumHullPoints.Value, 0, 1) : 0;
    private static float MemberShield(IEnbVitalsFrame? v) => v?.ShieldPercent is float s && float.IsFinite(s) ? Math.Clamp(s, 0, 1) : 0;

    /// <summary>The member row a group key belongs to ("group/member/2", "group/hull/2", ...).</summary>
    private static int? MemberIndex(string key)
    {
        int slash = key.LastIndexOf('/');
        return slash > 0 && int.TryParse(key.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int i) && i < MaxMembers ? i : null;
    }

    private string? MemberTip(string key)
    {
        if (key == "group/formation") return "Your group leader has chosen a formation.\nClick here, or right-click a group member, then Form up.";
        if (MemberIndex(key) is not int i) return null;
        if (key.StartsWith("group/reactor/", StringComparison.Ordinal))
            return i < members.Length && members[i].Reactor is int reactor ? "Reactor: " + reactor.ToString(CultureInfo.InvariantCulture) + "%" : null;
        var v = memberVitals[i];
        bool hull = key.StartsWith("group/hull/", StringComparison.Ordinal);
        return hull ? gaugeTooltip?.FormatGauge(true, v?.HullPoints, v?.MaximumHullPoints)
            : gaugeTooltip?.FormatGauge(false, v?.ShieldPercent * v?.MaximumShieldPower, v?.MaximumShieldPower);
    }
}
