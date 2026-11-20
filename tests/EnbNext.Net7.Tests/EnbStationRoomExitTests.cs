using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbStationRoomExitTests
{
    [Fact]
    public void RoomTypeOutsideTheFourDoorTablesHasNoExit()
    {
        // 0063FCB0 indexes its 4x4 door tables (00B8AABC, 00B8AA7C) by room type without
        // a bound; type 4 and above read the adjacent tables as pivot-name pointers.
        // Safety rule: no exit is taken.
        var room = ((EnbCurrentRetailStationInterior)RuntimeHelpers.GetUninitializedObject(typeof(EnbCurrentRetailStationInterior))) with { RoomType = 4 };
        var definition = new EnbSceneStationDefinition(0, [new EnbSceneStationRoom(0, 0, ImmutableArray<uint>.Empty)]);
        Assert.Null(EnbStationRooms.FindExit(room, definition, Vector3.Zero));
    }
}
