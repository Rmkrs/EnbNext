using EnbNext.Presentation;

namespace EnbNext.Rendering;

[Flags]
public enum EnbRetailTargetCategory : uint
{
    None = 0,
    Player = 0x02,
    Navigation = 0x08,
    Object = 0x10,
    Self = 0x20,
    Group = 0x40,
    Attacking = 0x80,
    Attackable = 0x100,
    Friendly = 0x200,
}

/// <summary>
/// Current-client target classification and distance-ordered wraparound used by
/// BuildSortedTargetCandidateList (0x007C4530), FindNextTargetCandidate
/// (0x007C3FF0) and FindPreviousTargetCandidate (0x007C4120).
/// </summary>
public static class EnbCurrentRetailTargetingRuntime
{
    /// <summary>
    /// Projects retained verb slots into the authored scanner row. Initial 0x005B
    /// insertion is ascending; subsequent 0x005C updates preserve existing slots.
    /// BuildSelectedTargetVerbButtons consumes ascending values as button_00,
    /// button_01, ... while the W3D animation places button_00 at screen-right.
    /// </summary>
    public static uint[] OrderVerbButtonsLeftToRight(IEnumerable<uint> ascendingVerbs)
    {
        ArgumentNullException.ThrowIfNull(ascendingVerbs);
        return ascendingVerbs.Take(5).Reverse().ToArray();
    }

    public static uint? SelectAdjacent(
        EnbRadarFrame frame,
        uint? currentTargetId,
        bool previous,
        bool preserveCurrentCategory)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.IsAvailable || frame.Contacts.IsDefaultOrEmpty)
            return null;

        EnbRadarContact? current = currentTargetId is uint id
            ? frame.Contacts.FirstOrDefault(contact => contact.GameId == id)
            : null;
        EnbRadarContact[] candidates = BuildCandidates(frame, current, preserveCurrentCategory);
        if (candidates.Length == 0)
            return null;

        int currentIndex = currentTargetId is uint selected
            ? Array.FindIndex(candidates, contact => contact.GameId == selected)
            : -1;
        if (currentIndex < 0)
            return previous ? candidates[^1].GameId : candidates[0].GameId;

        int next = previous
            ? (currentIndex + candidates.Length - 1) % candidates.Length
            : (currentIndex + 1) % candidates.Length;
        return candidates[next].GameId;
    }

    public static uint SelectNearest(EnbRadarFrame frame, EnbRetailTargetCategory category)
    {
        ArgumentNullException.ThrowIfNull(frame);
        // 007C42E0/4310/4340/4370 set a category and start at null.
        // Player has a distinct retail algorithm; do not route it here.
        if (category is not (EnbRetailTargetCategory.Attackable or EnbRetailTargetCategory.Friendly or
            EnbRetailTargetCategory.Navigation or EnbRetailTargetCategory.Object or EnbRetailTargetCategory.Attacking))
            throw new ArgumentOutOfRangeException(nameof(category));
        if (!frame.IsAvailable) return 0;
        return frame.Contacts.Where(c => (Classify(c) & category) == category)
            .OrderBy(c => c.SurfaceDistance).ThenBy(c => c.RenderIdentity)
            .Select(c => c.GameId).FirstOrDefault();
    }

    public static uint SelectNearestPlayer(EnbRadarFrame frame, IEnbRenderObject observer)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(observer);
        if (!frame.IsAvailable) return 0;
        // 007C4270 advances twice from null. Native candidates include self;
        // the radar projection deliberately does not, so restore it only here.
        var candidates = frame.Contacts.Where(c => c.ObjectType == 1)
            .Select(c => (c.GameId, c.SurfaceDistance, c.RenderIdentity)).ToList();
        if (observer.ObjectType == 1 && !candidates.Any(c => c.GameId == observer.GameId))
            candidates.Add((observer.GameId, 0, observer.RenderIdentity));
        var ordered = candidates.OrderBy(c => c.SurfaceDistance).ThenBy(c => c.RenderIdentity).ToArray();
        return ordered.Length == 0 ? 0 : ordered[ordered.Length == 1 ? 0 : 1].GameId;
    }

    public static uint? SelectGroupMember(EnbSceneGroupSnapshot? group, IReadOnlyList<IEnbRenderObject> objects, int index)
    {
        // 007C43A0 checks GroupInfo state, then the retained slot ID. Name and radar range are not gates.
        if ((uint)index >= 5 || group is null || group.AuxDataState == 0) return null;
        uint id = group.Members.FirstOrDefault(m => m.Index == index)?.GameId ?? 0;
        if (id == 0) return null;
        return objects.Any(o => o.GameId == id) ? id : 0;
    }

    public static int CountCandidates(
        EnbRadarFrame frame,
        uint? currentTargetId,
        bool preserveCurrentCategory)
    {
        ArgumentNullException.ThrowIfNull(frame);
        EnbRadarContact? current = currentTargetId is uint id
            ? frame.Contacts.FirstOrDefault(contact => contact.GameId == id)
            : null;
        return BuildCandidates(frame, current, preserveCurrentCategory).Length;
    }

    private static EnbRadarContact[] BuildCandidates(
        EnbRadarFrame frame,
        EnbRadarContact? current,
        bool preserveCurrentCategory)
    {
        EnbRetailTargetCategory filter = preserveCurrentCategory && current is not null
            ? Classify(current)
            : EnbRetailTargetCategory.None;
        return frame.Contacts
            .Where(contact => filter == EnbRetailTargetCategory.None ||
                (Classify(contact) & filter) == filter)
            .OrderBy(static contact => contact.SurfaceDistance)
            .ThenBy(static contact => contact.RenderIdentity)
            .ToArray();
    }

    public static EnbRetailTargetCategory Classify(EnbRadarContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        // 007C4940 carries object+130 through every classification branch.
        var attacking = contact.IsAttacking ? EnbRetailTargetCategory.Attacking : EnbRetailTargetCategory.None;
        if (contact.ObjectType == 0x01)
        {
            EnbRetailTargetCategory player = EnbRetailTargetCategory.Player | attacking;
            if (contact.IsGroupMember)
                player |= EnbRetailTargetCategory.Group;
            if (contact.IsPvpAttackable)
                player |= EnbRetailTargetCategory.Attackable;
            return player;
        }

        if (contact.IsNavigation)
            return EnbRetailTargetCategory.Navigation | attacking;

        if (IsObjectCategory(contact.ObjectType))
            return EnbRetailTargetCategory.Object | attacking;

        return contact.RelationshipRaw <= 1
            ? EnbRetailTargetCategory.Attackable | attacking
            : EnbRetailTargetCategory.Friendly | attacking;
    }

    private static bool IsObjectCategory(byte objectType)
        => objectType is not (0x00 or 0x01 or 0x02 or 0x0B or 0x0C or 0x1C or 0x21);
}
