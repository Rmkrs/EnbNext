using System.Numerics;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;
[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbFrameStreamPublicationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void PublicationOwnershipRemovesCopyWorkWithoutChangingThePublishedRange()
    {
        const int count=4096,frames=4000;
        var arena=new EnbW3dTransientGeometryArena();
        long checksum=0;
        void Draw(bool transfer)
        {
            using var frame=arena.BeginFrame();var stream=frame.RentPositions(count);
            stream.WriteSpan.Fill(new(1,2,3));
            if(transfer)stream.SealForPublication();
            var published=stream.RetainForPublication();checksum+=(long)published[count-1].Z;
        }
        for(int i=0;i<1000;i++){Draw(false);Draw(true);}
        var runs=new List<object>();
        foreach(bool transfer in new[]{false,true,true,false})
        {
            var cpu=System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            long start=GC.GetAllocatedBytesForCurrentThread();var watch=System.Diagnostics.Stopwatch.StartNew();
            for(int i=0;i<frames;i++)Draw(transfer);
            var wall=watch.Elapsed.TotalMilliseconds;long bytes=GC.GetAllocatedBytesForCurrentThread()-start;
            runs.Add(new{Transfer=transfer,Frames=frames,Vertices=count,WallMsPerFrame=wall/frames,
                ProcessCpuMsPerFrame=(System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime-cpu).TotalMilliseconds/frames,
                BytesPerFrame=(double)bytes/frames});
        }
        Assert.Equal(54000,checksum);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(runs));
    }
    [Fact]
    public void SealedPublicationSurvivesDrainReuseNestedViewAndDuplicateDispose()
    {
        var arena=new EnbW3dTransientGeometryArena();
        var frame=arena.BeginFrame();var stream=frame.RentPositions(3);
        stream.WriteSpan.Fill(new Vector3(1,2,3));stream.SealForPublication();
        var published=stream.RetainForPublication();
        Assert.Equal(3,published.Count);Assert.Throws<ArgumentOutOfRangeException>(()=>published[3]);
        Assert.Throws<InvalidOperationException>(()=>{_=stream.WriteSpan.Length;});
        Assert.Same(published,stream.RetainForPublication());
        using(var nested=arena.BeginFrame()){nested.RentPositions(100).WriteSpan.Fill(new(4));}
        frame.Dispose();
        Assert.Throws<ObjectDisposedException>(()=>stream[0]);
        Assert.Same(published,stream.RetainForPublication());
        using(var next=arena.BeginFrame())
        {
            next.RentPositions(3).WriteSpan.Fill(new(9));frame.Dispose();
            Assert.All(published,v=>Assert.Equal(new Vector3(1,2,3),v));
        }
    }
    [Fact]
    public void UnsealedPublicationRemainsASnapshotAndUnpublishedSealedRangesReuseStorage()
    {
        var arena=new EnbW3dTransientGeometryArena();
        using(var frame=arena.BeginFrame())
        {
            var stream=frame.RentPositions(3);stream.WriteSpan.Fill(new(1));
            var published=stream.RetainForPublication();stream.WriteSpan.Fill(new(2));
            Assert.All(published,v=>Assert.Equal(new Vector3(1),v));
        }
        long allocations=arena.ArrayAllocations;
        using(var frame=arena.BeginFrame()){frame.RentPositions(3).SealForPublication();}
        using(var frame=arena.BeginFrame()){frame.RentPositions(3).WriteSpan.Fill(new(3));}
        Assert.Equal(allocations,arena.ArrayAllocations);
    }
    [Fact]
    public void SealedPublicationDoesNotAllocateOrCopyAVertexArray()
    {
        var arena=new EnbW3dTransientGeometryArena();
        using var frame=arena.BeginFrame();var stream=frame.RentPositions(100000);
        stream.WriteSpan.Fill(new(5));stream.SealForPublication();
        long before=GC.GetAllocatedBytesForCurrentThread();var publication=stream.RetainForPublication();
        long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
        Assert.InRange(bytes,0,128);Assert.Equal(100000,publication.Count);
        Assert.Equal(new Vector3(5),publication[99999]);
    }
}
