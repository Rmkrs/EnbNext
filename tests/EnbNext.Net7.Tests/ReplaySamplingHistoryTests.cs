using EnbNext.Net7.Diagnostics;
namespace EnbNext.Net7.Tests;
public sealed class ReplaySamplingHistoryTests
{
    [Fact]
    public void StationaryCameraSmoothingHasCadenceDependentFloatQuantization()
    {
        var target=new System.Numerics.Vector3(861.09f,54202.95f,95.94f);
        System.Numerics.Vector3 Settle(int dt)
        {
            var position=target+new System.Numerics.Vector3(0,100,0);
            var orientation=System.Numerics.Quaternion.Identity;
            for(int time=0;time<20000;time+=dt)
                (position,orientation)=EnbNext.Rendering.EnbRetailCameraSmoothing.Update(
                    position,orientation,target,orientation,dt);
            return position;
        }
        var a=Settle(8);var b=Settle(16);
        int ulps=Math.Abs(BitConverter.SingleToInt32Bits(a.Y)-BitConverter.SingleToInt32Bits(b.Y));
        Assert.True(ulps>4,$"A four-ULP replay camera gate cannot represent normal smoothing: {ulps} ULPs");
        // At positive integral-millisecond updates alpha >= .001. Float
        // rounding can stop an axis up to half an ULP / alpha from its target.
        float ulp=MathF.BitIncrement(target.Y)-target.Y;
        Assert.InRange(MathF.Abs(a.Y-target.Y),0,ulp*500);
        Assert.InRange(MathF.Abs(b.Y-target.Y),0,ulp*500);
    }

    [Fact]
    public void PendingInputPreventsPresentationFromAdvancingPastItsCommandBoundary()
    {
        var live=EnbR11FlightTests.FlightWorld();
        var late=EnbR11FlightTests.FlightWorld();
        var bounded=EnbR11FlightTests.FlightWorld();
        foreach(var world in new[]{live,late,bounded})world.ApplyLocalFlightControls(7,1000,.83f,.02f,null);
        var clock=new Net7WireReplayClock(1301);
        live.TrySampleObject(7,1299,out _);
        late.TrySampleObject(7,clock.GetMilliseconds(),out _);
        clock.LimitSimulationTimeUntilInput(1299);
        Assert.Equal(1299u,clock.GetMilliseconds());
        bounded.TrySampleObject(7,clock.GetMilliseconds(),out _);
        foreach(var world in new[]{live,late,bounded})world.ApplyLocalFlightControls(7,1299,0,0,null);
        live.TrySampleObject(7,2000,out var expected);
        late.TrySampleObject(7,2000,out var wrong);
        bounded.TrySampleObject(7,2000,out var actual);
        Assert.NotEqual(expected.Orientation,wrong.Orientation);
        Assert.Equal(expected,actual);
        clock.LimitSimulationTimeUntilInput(null);
        Assert.Equal(1301u,clock.GetMilliseconds());
    }
}
