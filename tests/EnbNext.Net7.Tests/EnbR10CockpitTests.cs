using EnbNext.Client;
using System.Numerics;
using EnbNext.App;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10CockpitTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void KeyboardLockPromotesHeldThrustAndOppositeLockStopsFirst(int direction)
    {
        var state = new EnbCockpitThrustState();
        state.ApplyKeyboardLock(direction, false, direction);
        Assert.True(state.Locked); Assert.False(state.UseLockedMove);
        state.Release(); Assert.Equal(direction, state.Direction);
        state.ApplyKeyboardLock(direction, false, direction);
        Assert.Equal(direction, state.Direction);
        state.ApplyKeyboardLock(-direction, false, direction);
        Assert.Equal(0, state.Direction);
        state.ApplyKeyboardLock(-direction, false, 0);
        Assert.True(state.UseLockedMove); Assert.Equal(-direction, state.Direction);
        state.Clear(); Assert.False(state.Locked);
    }

    [Fact]
    public void ForwardToggleStopsExistingLockAndStartsLockedFromRest()
    {
        var state = new EnbCockpitThrustState();
        state.ApplyKeyboardLock(1, true, 0);
        Assert.True(state.UseLockedMove); Assert.Equal(1, state.Direction);
        state.ApplyKeyboardLock(1, true, 1);
        Assert.Equal(0, state.Direction);
    }

    [Theory]
    [InlineData(0f,"000")]
    [InlineData(.15f,"150")]
    [InlineData(-.05f,"-50")]
    [InlineData(-.0099f,"-09")]
    [InlineData(.0099f,"009")]
    public void SpeedUsesSignedVelocityInUnitsPerSecond(float velocity, string expected)
        => Assert.Equal(expected, EnbCockpitThrustState.SpeedText(new EnbSceneTransformSnapshot(0,Vector3.Zero,
            Quaternion.Identity,new(velocity,0,0),999,Vector3.Zero,Vector3.UnitX)));

    [Fact]
    public void ClickLocksDefaultIsAuthoredAndBothButtonsStopALock()
    {
        Assert.True(EnbRetailPlayerOptionsCatalog.ParseText("").CpitThrottleClickLocks);
        Assert.False(EnbRetailPlayerOptionsCatalog.ParseText("[BOOL-1]\nOptionName=Cpit_ThrottleClickLocks\nOptionValue=no").CpitThrottleClickLocks);
        var state = new EnbCockpitThrustState();
        state.Press(1,true); state.Release(); Assert.Equal(1,state.Direction); Assert.True(state.Locked);
        state.Press(-1,true); Assert.Equal(0,state.Direction);
        state.Press(-1,true); state.Release(); Assert.Equal(-1,state.Direction);
        state.Clear(); Assert.Equal(0,state.Direction); Assert.False(state.Locked);
        state.Press(1,false); Assert.Equal(1,state.Direction); state.Release(); Assert.Equal(0,state.Direction);
    }

    [Theory]
    [InlineData(1,false,0)] [InlineData(-1,false,1)]
    [InlineData(1,true,2)] [InlineData(-1,true,3)] [InlineData(0,false,4)]
    public void ClickAndHeldThrustUseProvenMoveKinds(int direction, bool locked, byte expected)
    {
        var input = new Net7FlightInput(0,0,direction) { ThrustLocked = locked };
        input.Validate(); Assert.Equal(expected,input.MoveType);
    }

    [Fact]
    public void MomentaryLongHoldLatchesLocallyWithoutSendingAnotherMoveKind()
    {
        var state = new EnbCockpitThrustState();
        state.Press(1,false,100);
        state.AdvanceHeld(1100,true); Assert.False(state.Locked);
        state.AdvanceHeld(1101,true); Assert.True(state.Locked); Assert.False(state.UseLockedMove);
        state.Release(); Assert.Equal(1,state.Direction);
        state.Press(1,false,1200); Assert.Equal(0,state.Direction);
        state.Press(-1,false,2000); state.AdvanceHeld(2900,false);
        state.AdvanceHeld(3900,true); Assert.False(state.Locked);
        state.Release(); Assert.Equal(0,state.Direction);
    }
}
