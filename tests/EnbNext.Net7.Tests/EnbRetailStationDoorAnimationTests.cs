using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailStationDoorAnimationTests
{
    [Fact]
    public void ProximityChangeWaitsForSlotCompletionRatherThanReversingMidAnimation()
    {
        var door=new EnbRetailStationDoorAnimation(31,10,1000);
        Assert.Null(door.Update(false,1000));
        Assert.True(door.Update(true,1000));
        Assert.Equal(0,door.Frame);
        Assert.Null(door.Update(false,2000));
        Assert.True(door.Open);
        Assert.Equal(10,door.Frame,4);
        Assert.Null(door.Update(false,4100));
        Assert.Equal(30,door.Frame);
        Assert.True(door.Complete);
        Assert.False(door.Update(false,4100));
        Assert.Equal(31,door.Progress);
        Assert.Equal(30,door.Frame);
        Assert.Null(door.Update(true,5100));
        Assert.False(door.Open);
        Assert.Equal(21,door.Frame,4);
        Assert.Null(door.Update(true,8100));
        Assert.True(door.Frame<0); // explicit reverse binding is not a clamped forward clock
        Assert.True(door.Update(true,8100));
        Assert.Equal(0,door.Frame);
    }

    [Fact]
    public void ANewRoomSlotIsClosedAndDoesNotEmitAClosingCue()
    {
        var door=new EnbRetailStationDoorAnimation(31,10,uint.MaxValue-20);
        Assert.True(door.Update(true,uint.MaxValue-20));
        Assert.Null(door.Update(true,79));
        Assert.Equal(1,door.Frame,4);
        var replacement=new EnbRetailStationDoorAnimation(31,10,79);
        Assert.Null(replacement.Update(false,79));
        Assert.Equal(0,replacement.Frame);
    }

    [Theory]
    [InlineData(0,"terren_station_door_1.wav","terran_station_door_close")]
    [InlineData(1,"jenquai_station_door_1.wav","jenquai_station_door_close")]
    [InlineData(2,"progen_station_door_1.wav","progen_station_door_close")]
    [InlineData(9,"terren_station_door_1.wav","terran_station_door_close")]
    public void TransitionCuesUseTheRetailTable(uint type,string open,string close)
    {
        Assert.Equal(open,EnbRetailStationDoorAnimation.Sound(type,true));
        Assert.Equal(close,EnbRetailStationDoorAnimation.Sound(type,false));
    }

    [Fact]
    public void MissingOptionalAnimationRetainsProximityAndCueTransitions()
    {
        var door=new EnbRetailStationDoorAnimation(0,0,0);
        Assert.True(door.Update(true,0));
        Assert.Null(door.Update(true,50));
        Assert.False(door.Update(false,60));
        Assert.True(door.Complete);
        Assert.Equal(0,door.Frame);
    }
}
