using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbClientViewLightingStateTests
{
    [Fact]
    public void FreshDeviceAndCompletedClientViewTailAreDistinct()
    {
        var state = new EnbCurrentRetailClientViewLightingState();
        var firstCapture = state.CaptureEnvironment;
        Assert.Equal(0u,firstCapture.GlobalAmbientRgb24);
        state.CompleteClientViewFrame();
        Assert.Equal(0x7f7f7fu,state.CaptureEnvironment.GlobalAmbientRgb24);
        Assert.Equal(0u,firstCapture.GlobalAmbientRgb24);
        state.ResetDevice();
        Assert.Equal(0u,state.CaptureEnvironment.GlobalAmbientRgb24);
        state.CompleteClientViewFrame();
        Assert.Equal(0x7f7f7fu,state.CaptureEnvironment.GlobalAmbientRgb24);
    }
}
