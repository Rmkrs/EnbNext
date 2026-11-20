using System.Collections.Immutable;
using EnbNext.Presentation;

namespace EnbNext.Engine;

/// <summary>
/// Keeps one character's tracked-mission list current against the mission log: completed, failed
/// and expired missions drop out, as do missions that vanished between two non-empty logs (forfeited
/// or cleared); newly given missions are added when auto-track is on.
/// </summary>
public static class EnbTrackedMissions
{
    /// <summary>Returns true when <paramref name="tracked"/> changed.</summary>
    public static bool Update(List<int> tracked, ImmutableArray<EnbCharacterMissionSnapshot> missions, ImmutableArray<EnbCharacterMissionSnapshot> previous,
        IEnumerable<EnbCharacterMissionSnapshot> newlyGiven, bool autoTrack)
    {
        bool changed = false;
        if (autoTrack)
            foreach (var mission in newlyGiven)
                if (!mission.CanClear && !tracked.Contains(mission.Id)) { tracked.Add(mission.Id); changed = true; }
        changed |= tracked.RemoveAll(id => Find(missions, id) is { } held ? held.CanClear : !missions.IsEmpty && Find(previous, id) is not null) > 0;
        return changed;
    }

    private static EnbCharacterMissionSnapshot? Find(ImmutableArray<EnbCharacterMissionSnapshot> missions, int id)
    {
        foreach (var mission in missions) if (mission.Id == id) return mission;
        return null;
    }
}
