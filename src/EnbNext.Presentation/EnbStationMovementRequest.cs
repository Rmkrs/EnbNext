using System.Numerics;
namespace EnbNext.Presentation;
public enum EnbStationInteractionPose : uint { Normal = 0, Terminal = 0x10, Npc = 0x20 }
public readonly record struct EnbStationMovementRequest(uint StarbaseId, ulong HandoffVersion, Vector3 Position, float Heading, bool Run)
{
    public uint RoomType { get; init; }
    public EnbStationInteractionPose Interaction { get; init; }
}
/// <summary>Where a multibox leader stands inside its station.</summary>
public readonly record struct EnbStationFollowTarget(uint StarbaseId,uint Room,Vector3 Position,bool Run);
public readonly record struct EnbStationRoomRequest(uint StarbaseId,ulong HandoffVersion,uint Source,uint Destination);
public static class EnbStationRoomLayout
{
    // Current retail wire/view room order, 00B8AB3C (forward, left, back, right).
    private static readonly int[,] Destinations={{1,-1,-1,-1},{-1,3,0,2},{-1,1,-1,-1},{-1,-1,-1,1}};
    public static int Destination(uint source,int door) => source<4 && door>=0 && door<4 ? Destinations[source,door] : -1;
    public static bool AreConnected(uint source,uint destination)
        => source<4 && destination<4 && Enumerable.Range(0,4).Any(i=>Destination(source,i)==destination);
}
