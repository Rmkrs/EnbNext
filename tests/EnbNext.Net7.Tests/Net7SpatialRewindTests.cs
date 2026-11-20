using System.Numerics;
using EnbNext.Net7.World;

namespace EnbNext.Net7.Tests;

public sealed class Net7SpatialRewindTests
{
    private static Net7AdvancedPositionUpdate Update(float current,float set)
        => new(0x103,7,1000,new(100,0,0),Quaternion.Identity,1,current,set,0,0,0,0,0,Vector3.Zero,0,0,0,100);

    private static Vector3 SampleBefore(Net7AdvancedPositionUpdate update)
    {
        var model=new Net7AdvancedKeyframeSpatialModel(update,1000);
        Assert.True(model.TrySample(new Net7WorldState(),500,new Net7SpatialSamplingContext(500),out var sample));
        return sample.Position;
    }

    [Fact]
    public void EarlierSamplesRewindOnlyUnderTheRetailPositiveCondition()
    {
        // 00711520: a cruising keyframe (both speeds >= 1e-6, no drift or acceleration) rewinds.
        Assert.True(SampleBefore(Update(5,5)).X < 100);
        // A stationary keyframe keeps the packet transform.
        Assert.Equal(100,SampleBefore(Update(0,0)).X);
        // NaN fails the x87 compares, so retail keeps the packet transform too.
        Assert.Equal(100,SampleBefore(Update(float.NaN,5)).X);
    }
}
