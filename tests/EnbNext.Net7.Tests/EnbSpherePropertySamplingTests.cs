using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSpherePropertySamplingTests
{
    private static EnbCurrentRetailSphereHeader Header()=>new(1,9,"test",Vector3.Zero,Vector3.One,1,
        new(.2f,.4f,.6f),.75f,new(2,3,4),[0,0,0,1,1],"",new byte[16]);

    [Fact]
    public void HsvShiftsKeysBeforeInterpolationAndPreservesOtherTracksAndSource()
    {
        var original=new EnbRetailSphereProperties(Header(),new Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>>
        {
            [2]=[new([1,0,0],0),new([0,1,0],2)],
            [3]=[new([.2f],0),new([.8f],2)],
            [4]=[new([1,2,3],0),new([3,4,5],2)],
            [5]=[new([0,0,0,1,1],0),new([0,0,1,0,3],2)]
        });
        var shifted=original.ShiftColors(new(60,0,0));
        Assert.Same(original,original.ShiftColors(Vector3.Zero));
        Assert.Equal(original.InitialValues,shifted.InitialValues);
        Assert.True(Vector3.Distance(new(.5f,1,.5f),shifted.Evaluate(1).Color)<.00001f);
        Assert.Equal(new Vector3(.5f,.5f,0),original.Evaluate(1).Color);
        foreach(float age in new[]{0f,.5f,1f,2f,9f})
            Assert.Equal(original.Evaluate(age) with {Color=shifted.Evaluate(age).Color},shifted.Evaluate(age));
        Assert.Equal(shifted.Evaluate(2),shifted.Evaluate(9));
    }

    [Fact]
    public void HsvWithoutColorKeysChangesStaticColorOnly()
    {
        var original=new EnbRetailSphereProperties(Header(),new Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>>());
        var shift=new Vector3(60,.1f,.2f);
        var shifted=original.ShiftColors(shift);
        Assert.Equal(original.InitialValues with {Color=EnbCurrentRetailHsvRuntime.ShiftRgb(Header().Color,shift)},shifted.InitialValues);
        Assert.Equal(shifted.InitialValues,shifted.Evaluate(20));
        Assert.Equal(Header().Color,original.InitialValues.Color);
    }

    [Fact]
    public void DirectionSlerpsWhileStrengthAndOtherTracksInterpolateLinearly()
    {
        var sampler=new EnbRetailSphereProperties(Header(),new Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>>
        {
            [5]=[new([0,0,0,1,1],0),new([0,0,1,0,3],1)],
            [4]=[new([1,2,3],0),new([3,6,9],1)],
            [3]=[new([0],0),new([1],1)],
            [2]=[new([0,0,0],0),new([1,2,3],1)]
        });
        var sample=sampler.Evaluate(.5f);
        Assert.InRange(sample.Orientation.Z,.70710f,.70712f);
        Assert.InRange(sample.Orientation.W,.70710f,.70712f);
        Assert.Equal(2,sample.Strength);Assert.Equal(new Vector3(2,4,6),sample.Scale);
        Assert.Equal(.5f,sample.Opacity);Assert.Equal(new Vector3(.5f,1,1.5f),sample.Color);
        Assert.Equal(sampler.Evaluate(1),sampler.Evaluate(100));
    }

    [Fact]
    public void StaticFallbackAndAntipodalKeysPreserveAuthoredValues()
    {
        var header=Header();
        var empty=new EnbRetailSphereProperties(header,new Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>>());
        Assert.Equal(header.Color,empty.Evaluate(.8f).Color);Assert.Equal(header.Parameter54,empty.Evaluate(.8f).Scale);
        var keys=new EnbCurrentRetailEffectPropertyKey[]{new([0,0,0,1,2],0),new([0,0,0,-1,4],2)};
        var sampler=new EnbRetailSphereProperties(header,new Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>>{[5]=keys});
        keys[0].Value[3]=100; // Caller mutations cannot corrupt the validated sampler.
        Assert.Equal(Quaternion.Identity,sampler.Evaluate(1).Orientation);
        Assert.Equal(3,sampler.Evaluate(1).Strength);
        Assert.Equal(new Quaternion(0,0,0,-1),sampler.Evaluate(2).Orientation);
    }

    [Fact]
    public void InvalidTracksAndProgressAreRejectedAtTheBoundedSampler()
    {
        foreach(var keys in new EnbCurrentRetailEffectPropertyKey[][]
        {
            [new([1],.1f)], [new([float.NaN],0)], [new([1],0),new([2],0)], [new([1,2],0)]
        })Assert.Throws<InvalidDataException>(()=>new EnbRetailSphereProperties(Header(),
            new Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>>{[3]=keys}));
        var sampler=new EnbRetailSphereProperties(Header(),new Dictionary<uint,IReadOnlyList<EnbCurrentRetailEffectPropertyKey>>());
        Assert.Throws<ArgumentOutOfRangeException>(()=>sampler.Evaluate(-1));
        Assert.Throws<ArgumentOutOfRangeException>(()=>sampler.Evaluate(float.NaN));
    }
}
