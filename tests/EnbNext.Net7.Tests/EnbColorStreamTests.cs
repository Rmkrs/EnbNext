using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbColorStreamTests
{
    [Fact] public void PackedSnapshotPreservesEveryByteConversionAndQueuedValues()
    {
        var colors=Enumerable.Range(0,256).Select(i=>new EnbRenderColor32((byte)i,(byte)(255-i),(byte)(i^0x55),(byte)(i^0xaa))).ToArray();
        var expected=colors.Select(c=>new Vector4(c.R*(1f/255f),c.G*(1f/255f),c.B*(1f/255f),c.A*(1f/255f))).ToArray();
        var snapshot=EnbW3dColorStream.Snapshot(colors);
        Array.Clear(colors);
        Assert.Equal(expected,snapshot.ToArray());
        EnbW3dColorStream interpolated=ImmutableArray.Create(new Vector4(.33333334f,1.1f,-.2f,.75f));
        Assert.Equal(new Vector4(.33333334f,1.1f,-.2f,.75f),interpolated[0]);
    }
    [Fact] public void ImmutableSourceAndWhiteNeedNoPerVertexExpansionStorage()
    {
        var colors=Enumerable.Repeat(new EnbRenderColor32(4,8,16,32),4096).ToImmutableArray();
        IReadOnlyList<EnbRenderColor32> source=colors;
        _=EnbW3dColorStream.Snapshot(source);_=EnbW3dColorStream.White(4096);
        long start=GC.GetAllocatedBytesForCurrentThread();
        var retained=EnbW3dColorStream.Snapshot(source);var white=EnbW3dColorStream.White(4096);
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-start);
        Assert.Equal(4096,retained.Count);Assert.Equal(Vector4.One,white[4095]);
    }
}
