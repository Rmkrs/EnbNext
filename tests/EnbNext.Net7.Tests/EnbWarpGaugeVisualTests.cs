using EnbNext.Net7.Gameplay;
using EnbNext.Rendering;
using System.Numerics;
namespace EnbNext.Net7.Tests;
public sealed class EnbWarpGaugeVisualTests
{
    [Fact] public void FrameCommandsRetainDeadlineAndRepeatWithoutValueChanges()
    {
        var gauge = new EnbWarpGaugeRuntime();
        var command = new EnbNext.Presentation.EnbSceneCockpitWarpCommand(1, 1, 5000);
        gauge.UpdateVisual(1, Status(1, 5000), 1000, command);
        Assert.Equal(.5f, gauge.UpdateVisual(1, Status(1, 9000), 3000, command).Fill);
        // Same state, new native callback: retained deadline is sampled again.
        command = new(2, 1, 5000);
        Assert.Equal(0, gauge.UpdateVisual(1, Status(1, 5000), 3000, command).Fill);
        Assert.Equal(.5f, gauge.UpdateVisual(1, Status(1, 5000), 4000, command).Fill);
        // An absent timer skips SetMode while the existing charge continues.
        command = new(3, 3, 5000, false);
        Assert.Equal(.75f, gauge.UpdateVisual(1, Status(3, 5000), 4500, command).Fill);
        command = new(4, 2, null);
        Assert.Equal(Vector3.UnitX, gauge.UpdateVisual(1, Status(2), 4500, command).ButtonEmission);
        gauge.UpdateVisual(1, Status(2), 5500, command);
        Assert.Equal(Vector3.UnitX, gauge.UpdateVisual(1, Status(2), 5500, command with { Sequence = 5 }).ButtonEmission);
    }
    private static Net7WarpStatus Status(int state, long end = 0, bool incapacitated = false)
        => new(state, 2, end, incapacitated, false, 150, 0);
    [Fact] public void ActiveRetainsPartialFillAndPulsesAtEightRadiansPerSecond()
    {
        var gauge = new EnbWarpGaugeRuntime();
        gauge.UpdateVisual(1, Status(1,5000),1000);
        Assert.Equal(.5f,gauge.UpdateVisual(1,Status(1,5000),3000).Fill);
        var entry=gauge.UpdateVisual(1,Status(2),3000);
        Assert.Equal(.5f,entry.Fill); Assert.Equal(Vector3.UnitX,entry.ButtonEmission);
        var later=gauge.UpdateVisual(1,Status(2),3250);
        Assert.Equal(.5f,later.Fill); Assert.Equal((float)((Math.Cos(2)+1)*.5),later.Red,5);
        Assert.Null(gauge.UpdateVisual(1,Status(2),4250).ButtonEmission);
    }
    [Fact] public void IncapacitationDoublesPulseAndRecoveryClearsFillAndRate()
    {
        var gauge=new EnbWarpGaugeRuntime();
        Assert.Equal(1,gauge.UpdateVisual(1,Status(0,incapacitated:true),1000).Fill);
        Assert.Equal((float)((Math.Cos(4)+1)*.5),gauge.UpdateVisual(1,Status(0,incapacitated:true),1250).Red,5);
        var recovered=gauge.UpdateVisual(1,Status(0),1500);
        Assert.Equal(0,recovered.Fill); Assert.Equal(1,recovered.Red);
        Assert.Equal(recovered,gauge.UpdateVisual(1,Status(0),2000));
    }
    [Fact] public void TooltipOnlyStatesRetainControllerAndRecoveryDrainRestoresRed()
    {
        var gauge=new EnbWarpGaugeRuntime();
        gauge.UpdateVisual(1,Status(1,5000),1000);
        gauge.UpdateVisual(1,Status(2),3000);
        Assert.Equal(.5f,gauge.UpdateVisual(1,Status(4),3250).Fill);
        var drain=gauge.UpdateVisual(1,Status(6,7000),4000);
        Assert.Equal(1,drain.Fill); Assert.Equal(1,drain.Red);
        var idle=gauge.UpdateVisual(1,Status(0),5500);
        Assert.Equal(.5f,idle.Fill); // state zero does not stop the retained drain.
        Assert.Equal(0,gauge.UpdateVisual(1,Status(0),7000).Fill);
    }
    [Fact] public void ReplacementExpiredTimerAndPlanetAdmissionDoNotLeakAnimation()
    {
        var gauge=new EnbWarpGaugeRuntime();
        gauge.UpdateVisual(1,Status(0,incapacitated:true),1000);
        Assert.Equal(new EnbWarpGaugeVisual(0,1,null),gauge.UpdateVisual(2,Status(1,500),2000));
        Assert.Equal(new EnbWarpGaugeVisual(0,1,null),gauge.UpdateVisual(3,Status(2) with {PresentationMode=1},3000));
        Assert.Equal(new EnbWarpGaugeVisual(0,1,null),gauge.UpdateVisual(4,Status(2) with {PresentationMode=5},3000));
    }
}
