using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbNormalStreamTests
{
    [Fact]
    public void ZeroNormalRangesKeepExactPositiveZeroWithoutCountSizedStorage()
    {
        var range=EnbW3dNormalStream.Zero(1000000);
        Assert.True(range.IsKnownFinite);Assert.Equal(1000000,range.Count);
        Assert.Equal(0,BitConverter.SingleToInt32Bits(range[999999].X));
        Assert.Equal(0,BitConverter.SingleToInt32Bits(range[999999].Y));
        Assert.Equal(0,BitConverter.SingleToInt32Bits(range[999999].Z));
        Assert.Throws<ArgumentOutOfRangeException>(()=>range[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(()=>range[1000000]);
        Assert.Throws<ArgumentOutOfRangeException>(()=>EnbW3dNormalStream.Zero(-1));
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int count=1;count<10000;count++)
        {
            var current=EnbW3dNormalStream.Zero(count);
            if(current.Count!=count||current[count-1]!=Vector3.Zero)throw new Exception("Invalid zero range");
        }
        Assert.Equal(before,GC.GetAllocatedBytesForCurrentThread());
    }
    [Fact]
    public void MutableSourceDetachesButImmutableAndImplicitSourcesRetainTheirValues()
    {
        Vector3[] source=[Vector3.UnitX,Vector3.UnitY];
        var detached=EnbW3dNormalStream.Snapshot(source);source[0]=Vector3.UnitZ;
        Assert.Equal(Vector3.UnitX,detached[0]);Assert.False(detached.IsKnownFinite);
        ImmutableArray<Vector3> immutable=[Vector3.UnitX,Vector3.UnitY];
        var a=EnbW3dNormalStream.Snapshot(immutable);var b=EnbW3dNormalStream.Snapshot(immutable);
        Assert.True(a.Equals(b));Assert.Equal(a.GetHashCode(),b.GetHashCode());
        var zero=EnbW3dNormalStream.Zero(2);Assert.True(zero.Equals(EnbW3dNormalStream.Snapshot(zero)));
        Assert.False(zero.Equals(EnbW3dNormalStream.Zero(3)));
        EnbW3dNormalStream words=[new(float.NaN,-0f,2)];
        Assert.False(words.IsKnownFinite);Assert.True(float.IsNaN(words[0].X));
        Assert.Equal(unchecked((int)0x80000000),BitConverter.SingleToInt32Bits(words[0].Y));
    }
}
