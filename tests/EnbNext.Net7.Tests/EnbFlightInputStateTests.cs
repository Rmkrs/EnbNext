using EnbNext.Client;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Tests;

public sealed class EnbFlightInputStateTests
{
    private static bool Press(EnbFlightInputState<int> state,int key,string action)
    {
        Assert.True(state.KeyDown(key,action,true,true,out bool changed,out _));return changed;
    }
    private static Net7FlightInput Resolve(EnbFlightInputState<int> state,EnbFlightPointer? pointer=null)
        =>state.Resolve(true,true,0,false,pointer);

    [Fact]
    public void FocusReleaseStaysNeutralUntilARealReleaseAndNewPress()
    {
        var state=new EnbFlightInputState<int>();
        Assert.True(Press(state,1,"Move Forward"));Press(state,2,"Turn Left");
        Assert.Equal(new(-1,0,1),Resolve(state));
        state.Suspend();Assert.Equal(default,state.Intent);
        Assert.False(Press(state,1,"Move Forward"));Assert.Equal(default,Resolve(state));
        state.KeyUp(1);Assert.True(Press(state,1,"Move Forward"));
        Assert.Equal(new(0,0,1),Resolve(state));
        state.KeyUp(1);Assert.Equal(default,Resolve(state));
    }
    [Fact]
    public void HeldPromotionPreservesMoveKindAndRetiresTheOldHeldKey()
    {
        var state=new EnbFlightInputState<int>();Press(state,1,"Move Forward");
        Assert.Equal(0,Resolve(state).MoveType);
        Assert.True(state.KeyDown(2,"Move Forward Lock",true,true,out bool changed,out bool release));
        Assert.True(changed);Assert.True(release);Assert.Equal(0,Resolve(state).MoveType);
        state.KeyUp(1);state.KeyUp(2);Assert.Equal(1,Resolve(state).Thrust);
        Press(state,3,"Move Backward Lock");Assert.Equal(4,Resolve(state).MoveType);
        Assert.False(Press(state,3,"Move Backward Lock"));
        state.KeyUp(3);Press(state,3,"Move Backward Lock");Assert.Equal(3,Resolve(state).MoveType);
    }
    [Fact]
    public void MouseSteeringOverridesKeyboardButStopOverridesMouseThrottle()
    {
        var state=new EnbFlightInputState<int>();Press(state,1,"Turn Left");
        Assert.Equal(new(1,-1,0),Resolve(state,new(1,1,true,false)));
        Assert.Equal(new(0,0,1),Resolve(state,new(.5f,.5f,true,false)));
        Press(state,2,"Stop");Assert.Equal(default,Resolve(state,new(.5f,.5f,true,false)));
        state.KeyUp(2);Assert.Equal(new(-1,0,0),Resolve(state));
    }
    [Fact]
    public void UnavailableLockCannotStartOnARepeatedPressAfterLoading()
    {
        var state=new EnbFlightInputState<int>();
        Assert.True(state.KeyDown(1,"Move Forward Lock",false,false,out bool changed,out bool release));
        Assert.False(changed);Assert.False(release);
        Assert.False(Press(state,1,"Move Forward Lock"));Assert.Equal(default,Resolve(state));
        state.KeyUp(1);Press(state,1,"Move Forward Lock");Assert.Equal(2,Resolve(state).MoveType);
        state.ResetAfterSendFailure();Assert.Equal(default,state.Intent);
    }
}
