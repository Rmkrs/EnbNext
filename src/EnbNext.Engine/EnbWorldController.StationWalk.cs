using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

// Next Modern UI Local and station map (no retail counterpart): clicking a place walks the avatar there, through the
// doors on the way, and on arrival opens a character or terminal as clicking it there does. The way is planned over the
// room's walking grid (EnbStationNavigation): through the middle of halls and doorways and around the people standing
// in the room; it is planned again as they move. Player input ends the walk.
internal sealed partial class EnbWorldController
{
    private sealed record StationWalk(uint StarbaseId, string Key, uint Room);
    private StationWalk? _stationWalk;
    private bool _stationWalking;
    private uint _stationWalkLegRoom = uint.MaxValue;
    private Vector2[]? _stationWalkPath;
    private int _stationWalkStep;
    private bool _stationWalkPartial;
    private long _stationWalkWaitSince;
    private long _stationWalkPlannedAt, _stationWalkMovedAt;
    private Vector3 _stationWalkPlannedGoal, _stationWalkMovedFrom;
    private const long StationWalkStall = 2500, StationWalkReplan = 1200;

    /// <summary>Walks to a station roster entry (its key) in the given room; "room/N" walks into room N.</summary>
    internal void WalkToStation(string key, uint room)
    {
        if (_stationKey is not { } station || !_stationActive) return;
        _stationWalk = new(station.Id, key, room);
        _stationWalkLegRoom = uint.MaxValue; _stationWalkWaitSince = 0;
        _stationManualUntil = 0;
    }

    private void EndStationWalk()
    {
        _stationWalk = null; _stationWalkPath = null;
        if (_stationWalking) { _stationWalking = false; _w3dWorld.StopStationMovement(); }
    }

    private void UpdateStationWalk(EnbSceneStationDefinition? definition)
    {
        if (_stationWalk is not { } walk) return;
        if (_stationKey is not { } key || key.Id != walk.StarbaseId || definition is null || !_stationActive) { EndStationWalk(); return; }
        long now = Environment.TickCount64;
        // Passing a door or typing in chat holds the walk; anything else that takes the controls ends it.
        if (_stationRoomChanging || _stationLoadedRoom is not { } room || _ui.Chat.Editing || _w3dWorld.StationAvatarPose is not { } pose)
        {
            if (_stationWalking) { _stationWalking = false; _w3dWorld.StopStationMovement(); }
            _stationWalkMovedAt = now;
            return;
        }
        if (!CanControlStation) { EndStationWalk(); return; }
        if (_stationWalkLegRoom != _stationRoom)
        {
            _stationWalkLegRoom = _stationRoom; _stationWalkPath = null;
            _stationWalkMovedAt = now; _stationWalkMovedFrom = pose.Position;
        }
        Vector3 goal; float arrive; uint? through = null;
        if (walk.Room == _stationRoom)
        {
            if (_w3dWorld.StationWalkGoal(walk.Key) is not { } target) { EndStationWalk(); return; }
            if (target.InReach)
            {
                EndStationWalk();
                if (target.Npc is { } npc)
                {
                    _w3dWorld.QueueInteractionSound("ButtonClick02.wav");
                    _ = _ui.Inventory.OpenStationNpc(key.Id, key.Version, _stationRoom, npc);
                }
                else if (target.Facility is { } facility)
                {
                    _w3dWorld.QueueInteractionSound("ButtonClick02.wav");
                    _ = _ui.Inventory.OpenStationTerminal(key.Id, key.Version, _stationRoom, facility);
                }
                return;
            }
            goal = target.Position;
            // Inside the reach a click there has (12 for people and booths, 11 for terminals), with a little to spare.
            arrive = walk.Key.StartsWith("terminal/", StringComparison.Ordinal) ? 9 : 10;
        }
        else if (EnbStationFollow.NextRoom(_stationRoom, walk.Room, definition) is uint next && EnbStationRooms.ExitPosition(room, next) is { } exit)
        { goal = exit; arrive = 3; through = next; }
        else { EndStationWalk(); return; }

        // Something in the way the plan cannot see: give up once the avatar stops getting anywhere.
        if (Vector3.Distance(pose.Position, _stationWalkMovedFrom) > 1) { _stationWalkMovedFrom = pose.Position; _stationWalkMovedAt = now; }
        else if (_stationWalking && now - _stationWalkMovedAt > StationWalkStall) { EndStationWalk(); return; }

        var here = new Vector2(pose.Position.X, pose.Position.Y);
        if (StationMapRoom(_stationRoom)?.Navigation is { } grid &&
            (_stationWalkPath is null || now - _stationWalkPlannedAt > StationWalkReplan || Vector3.Distance(goal, _stationWalkPlannedGoal) > 2))
        {
            Span<Vector2> people = stackalloc Vector2[32];
            int count = 0;
            foreach (var entry in _w3dWorld.StationRoster)
                if (count < people.Length && entry.InRoom && entry.Position is { } at && entry.Kind is EnbStationRosterKind.Player or EnbStationRosterKind.Character or EnbStationRosterKind.Vendor)
                    people[count++] = new(at.X, at.Y);
            _stationWalkPath = grid.FindPath(here, new(goal.X, goal.Y), arrive, people[..count]);
            // A door's exit point off the walking floor: plan to the middle of its doorway instead.
            if (_stationWalkPath is null && through is uint to && StationMapRoom(_stationRoom)?.Doors.FirstOrDefault(d => d.To == to) is { Out: var outward } door && outward != default)
                _stationWalkPath = grid.FindPath(here, door.At + outward * 2, 2, people[..count]);
            // No way all the way there (someone in the way, say): as close as it goes, never straight at a wall.
            _stationWalkPath ??= grid.FindPath(here, new(goal.X, goal.Y), arrive, people[..count], closest: true);
            _stationWalkPartial = _stationWalkPath is not null && Vector2.Distance(_stationWalkPath[^1], new(goal.X, goal.Y)) > arrive;
            if (!_stationWalkPartial) _stationWalkWaitSince = 0;
            _stationWalkStep = 0; _stationWalkPlannedAt = now; _stationWalkPlannedGoal = goal;
        }
        // Follow the plan a few cells ahead; at its end (or without one) head straight for the goal.
        Vector3 aim = goal;
        float ahead = new Vector2(goal.X - here.X, goal.Y - here.Y).Length();
        if (_stationWalkPath is { Length: > 1 } path)
        {
            int nearest = _stationWalkStep;
            float best = float.MaxValue;
            for (int i = _stationWalkStep; i < Math.Min(path.Length, _stationWalkStep + 12); i++)
            {
                float d = Vector2.DistanceSquared(path[i], here);
                if (d < best) { best = d; nearest = i; }
            }
            _stationWalkStep = nearest;
            int look = Math.Min(nearest + 4, path.Length - 1);
            if (nearest < path.Length - 1) aim = new(path[look].X, path[look].Y, pose.Position.Z);
            // At the end of a way that does not reach the goal: wait there (it is planned again as people move).
            else if (_stationWalkPartial)
            {
                if (_stationWalking) { _stationWalking = false; _w3dWorld.StopStationMovement(); }
                if (_stationWalkWaitSince == 0) _stationWalkWaitSince = now;
                else if (now - _stationWalkWaitSince > 2 * StationWalkStall) EndStationWalk();
                return;
            }
            ahead = (path.Length - 1 - nearest) * EnbStationNavigation.Cell;
        }
        var steer = EnbStationFollow.Steer(pose.Position, pose.Heading, aim, 0, false);
        if (steer.Arrived && aim == goal) { EndStationWalk(); return; }
        // Run along open stretches, walk the turns and the last few steps.
        var toAim = aim - pose.Position;
        float angle = MathF.Abs(MathF.IEEERemainder(MathF.Atan2(toAim.Y, toAim.X) - pose.Heading, MathF.Tau));
        bool run = ahead > 15 && angle < .35f;
        _stationWalking = true;
        _w3dWorld.SetStationMovement(steer.Thrust, steer.Turn, run);
    }
}
