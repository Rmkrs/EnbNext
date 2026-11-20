using System.Numerics;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public static class EnbStationRooms
{
    // Current client 0063FCB0 / tables 00B8AB3C and 00B8AA3C. Wire/view order.
    private static readonly string[] Exits=["CAM_DOOR_FD_OUT","CAM_DOOR_LF_OUT","CAM_DOOR_BK_OUT","CAM_DOOR_RT_OUT"];
    private static readonly string[] Entries=["CAM_DOOR_FD_IN","CAM_DOOR_LF_IN","CAM_DOOR_BK_IN","CAM_DOOR_RT_IN"];
    public static (uint Room,Vector3 Offset)? FindExit(EnbCurrentRetailStationInterior room,EnbSceneStationDefinition definition,Vector3 position)
    {
        if(room.RoomType>3)return null;
        for(int i=0;i<4;i++)
        {
            int destination=EnbStationRoomLayout.Destination(room.RoomType,i);
            if(destination<0 || !definition.Rooms.Any(r=>r.RoomType==destination))continue;
            // 0063FD03 reads 00B8AA7C (camera OUT pivots), not 00B8AA3C (door attachments).
            var delta=position-room.PivotTransform(Exits[i]).Translation; delta.Z=0;
            if(delta.Length()<5)return ((uint)destination,delta*-.5f);
        }
        return null;
    }
    /// <summary>The camera OUT pivot FindExit tests for the door from this room to <paramref name="destination"/>.</summary>
    public static Vector3? ExitPosition(EnbCurrentRetailStationInterior room,uint destination)
    {
        if(room.RoomType>3)return null;
        for(int i=0;i<4;i++)
            if(EnbStationRoomLayout.Destination(room.RoomType,i)==destination)return room.PivotTransform(Exits[i]).Translation;
        return null;
    }
    public static string EntryPivot(uint destination,uint source)
    {
        for(int i=0;i<4;i++)if(EnbStationRoomLayout.Destination(destination,i)==source)return Entries[i];
        throw new InvalidDataException("Station rooms are not connected.");
    }
}
