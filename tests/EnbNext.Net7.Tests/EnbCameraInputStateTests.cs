using EnbNext.Assets;
using EnbNext.Client;

namespace EnbNext.Net7.Tests;

public sealed class EnbCameraInputStateTests
{
    [Fact]
    public void UnchangedCoordinatesPrecedeModifierChangesAndFocusRetainsLastPosition()
    {
        var state=new EnbCameraInputState();
        Assert.True(state.Sample(.5,.5,EnbKeyModifiers.Control).Initial);state.Accept();
        Assert.Equal(EnbCameraPointerAction.Unchanged,state.Sample(.5,.5,EnbKeyModifiers.None).Action);
        Assert.True(state.Active);
        state.End();Assert.Equal(EnbCameraPointerAction.Unchanged,state.Sample(.5,.5,EnbKeyModifiers.Control).Action);
        Assert.True(state.Sample(.6,.5,EnbKeyModifiers.Control).Initial);
    }
    [Fact]
    public void ShiftAndControlAreRetainedTogetherForTheCameraOwnerAndAltEndsGesture()
    {
        var state=new EnbCameraInputState();
        var sample=state.Sample(.7,.4,EnbKeyModifiers.Control|EnbKeyModifiers.Shift);
        Assert.True(sample.Distance);Assert.True(sample.Control);state.Accept();
        Assert.False(state.Sample(.6,.4,EnbKeyModifiers.Control).Initial);
        Assert.Equal(EnbCameraPointerAction.End,state.Sample(.5,.4,EnbKeyModifiers.Alt|EnbKeyModifiers.Control).Action);
    }
    [Fact]
    public void WheelConsumesFractionAndMagnitudeOnceThenResetsOnMouseMovement()
    {
        var state=new EnbCameraInputState();
        Assert.False(state.ConsumeWheel(.4,out _));Assert.True(state.ConsumeWheel(2.7,out bool forward));Assert.True(forward);
        Assert.False(state.ConsumeWheel(.8,out _));state.ResetWheel();Assert.False(state.ConsumeWheel(.9,out _));
        Assert.True(state.ConsumeWheel(-2,out forward));Assert.False(forward);
        Assert.False(state.ConsumeWheel(double.NaN,out _));
    }
    [Fact]
    public void WheelAdmissionUsesGestureCaptureBeforeModifiersAndRejectsSamePosition()
    {
        var state=new EnbCameraInputState();
        Assert.False(state.AdmitWheel(.5,.5,EnbKeyModifiers.None));
        Assert.True(state.AdmitWheel(.5,.5,EnbKeyModifiers.Shift));
        Assert.False(state.AdmitWheel(.5,.5,EnbKeyModifiers.Shift));state.Accept();
        Assert.True(state.AdmitWheel(.6,.5,EnbKeyModifiers.Alt));
        state.End();Assert.False(state.AdmitWheel(.7,.5,EnbKeyModifiers.Alt|EnbKeyModifiers.Shift));
    }
}
