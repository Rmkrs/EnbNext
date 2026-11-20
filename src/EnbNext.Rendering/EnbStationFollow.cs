using System.Numerics;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Station follow (owner-approved multibox feature, no retail counterpart):
/// steer the local avatar toward the leader, or toward the door of the next room on the way.</summary>
public static class EnbStationFollow
{
    /// <summary>Default distance at which a follower stops behind a leader in the same room.</summary>
    public const float FollowDistance = 15;
    /// <summary>Beyond this distance the follower runs even when the leader walks.</summary>
    public const float RunDistance = 40;

    /// <summary>Thrust/turn for EnbStationMovement. Positive turn lowers the heading (00783B50).</summary>
    public static (int Thrust, float Turn, bool Run, bool Arrived) Steer(Vector3 position, float heading, Vector3 goal,
        float stopDistance, bool leaderRun)
    {
        var delta = goal - position; delta.Z = 0;
        float distance = delta.Length();
        if (distance <= stopDistance) return (0, 0, false, true);
        float angle = MathF.IEEERemainder(MathF.Atan2(delta.Y, delta.X) - heading, MathF.Tau);
        float turn = -Math.Clamp(angle, -1, 1);
        int thrust = MathF.Abs(angle) < .8f ? 1 : 0;
        return (thrust, turn, leaderRun || distance > RunDistance, false);
    }

    /// <summary>The first room after <paramref name="from"/> on the shortest door path to <paramref name="to"/>.</summary>
    public static uint? NextRoom(uint from, uint to, EnbSceneStationDefinition definition)
    {
        if (from == to) return null;
        var previous = new Dictionary<uint, uint>();
        var queue = new Queue<uint>(); queue.Enqueue(from);
        while (queue.Count > 0)
        {
            uint room = queue.Dequeue();
            for (int door = 0; door < 4; door++)
            {
                int next = EnbStationRoomLayout.Destination(room, door);
                if (next < 0 || (uint)next == from || previous.ContainsKey((uint)next) ||
                    !definition.Rooms.Any(r => r.RoomType == (uint)next)) continue;
                previous[(uint)next] = room;
                if ((uint)next == to)
                {
                    uint step = to;
                    while (previous[step] != from) step = previous[step];
                    return step;
                }
                queue.Enqueue((uint)next);
            }
        }
        return null;
    }
}
