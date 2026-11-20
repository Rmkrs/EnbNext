using EnbNext.Engine;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

/// <summary>Current retail 00635209..00635451: the sign over a connected door, by its destination room's class.</summary>
public sealed class EnbStationDoorSignTests
{
    private static EnbSceneStationDefinition Station(uint family, uint bazaarFlags = 0, uint loungeFlags = 0)
        => new(family, [new(0, 0, []), new(1, 0, []), new(2, bazaarFlags, []), new(3, loungeFlags, [])]);

    [Fact]
    public void ClassTableNamesEachDestination()
    {
        var terran = Station(0);
        Assert.Equal("sn_hangar", EnbWorldRenderer.StationDoorSign(terran, 0));
        Assert.Equal("sn_maindeck", EnbWorldRenderer.StationDoorSign(terran, 1));
        // 00698E24: wire room 2 is class 3 (merchants), wire room 3 is class 2 (NPC lounge).
        Assert.Equal("sn_merchants", EnbWorldRenderer.StationDoorSign(terran, 2));
        Assert.Equal("sn_npclounge", EnbWorldRenderer.StationDoorSign(terran, 3));
    }

    [Fact]
    public void FlaggedRoomsUseTheirFamilyTable()
    {
        // 006987A0 sets record byte +0B from wire flag 0x80; only the Jenquai and fourth family tables differ.
        Assert.Equal("sn_const", EnbWorldRenderer.StationDoorSign(Station(1, bazaarFlags: 0x80), 2));
        Assert.Equal("sn_merchants", EnbWorldRenderer.StationDoorSign(Station(1), 2));
        Assert.Equal("sn_casino", EnbWorldRenderer.StationDoorSign(Station(3, loungeFlags: 0x80), 3));
        Assert.Equal("sn_npclounge", EnbWorldRenderer.StationDoorSign(Station(0, loungeFlags: 0x80), 3));
        Assert.Equal("sn_merchants", EnbWorldRenderer.StationDoorSign(Station(3, bazaarFlags: 0x80), 2));
    }

    [Fact]
    public void AbsentRoomRecordHasNoSign()
        => Assert.Null(EnbWorldRenderer.StationDoorSign(new EnbSceneStationDefinition(0, [new(0, 0, []), new(1, 0, [])]), 2));
}
