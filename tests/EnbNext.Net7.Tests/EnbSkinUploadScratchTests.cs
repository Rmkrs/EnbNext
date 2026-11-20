using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Rendering;
using EnbNext.Rendering.OpenGL;
namespace EnbNext.Net7.Tests;
[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbSkinUploadScratchTests
{
    private static EnbOpenGlLitShaderUploadPlan Plan(int count,int frame,EnbW3dColorStream colors,EnbW3dUvStream uv,bool zeroNormals=false)
    {
        var positions=Enumerable.Range(0,count*3).Select(i=>i%7==0?-0f:i*.125f+frame*.0625f).ToImmutableArray();
        var indices=new EnbOpenGlIndexedUploadPlan(positions,[],[],EnbOpenGlIndexElementType.UnsignedShort,2,default,[]);
        EnbW3dNormalStream normals=zeroNormals?EnbW3dNormalStream.Zero(count):Enumerable.Range(0,count).Select(i=>new Vector3(i*.25f,-0f,frame*.125f)).ToImmutableArray();
        var lane=new EnbW3dProgrammableLightingLanePlan(default,default,default,default,default,default,normals,colors,uv,EnbW3dLightEnvironment.BlackNoLights,[],default,false,default);
        return new(lane,indices,default);
    }
    private static uint[] Bits(ReadOnlySpan<float> values)=>values.ToArray().Select(BitConverter.SingleToUInt32Bits).ToArray();
    [Fact] public void RetainedStaticStreamsMatchCompleteInterleaveAcrossChangingPoseAndReplacement()
    {
        var storage=new EnbOpenGlSkinUploadScratch();var other=new EnbOpenGlSkinUploadScratch();
        var colors=EnbW3dColorStream.Snapshot(Enumerable.Range(0,17).Select(i=>new EnbRenderColor32((byte)(i*13),(byte)(255-i),32,127)).ToImmutableArray());
        var uv=Enumerable.Range(0,17).Select(i=>new Vector2(i*.01f,-0f)).ToImmutableArray();
        for(int frame=0;frame<120;frame++)
        {
            if(frame==50)colors=Enumerable.Repeat(new Vector4(.33333334f,-0f,1.125f,.25f),17).ToImmutableArray();
            if(frame==80)uv=uv.Select(v=>v+new Vector2(.125f,.25f)).ToImmutableArray();
            var plan=Plan(17,frame,colors,uv,frame%5==0);var oracle=new float[17*12];plan.CopyVertexElementsTo(oracle);
            Assert.Equal(Bits(oracle),Bits(storage.Prepare(plan)));
            var diagnostic=plan.VertexElements.ToArray();
            var independent=Plan(17,frame+4,colors,uv);
            _=other.Prepare(independent);_=storage.Prepare(independent);
            Assert.Equal(Bits(oracle),Bits(diagnostic));Assert.Equal(Bits(oracle),Bits(plan.VertexElements.AsSpan()));
        }
        Assert.Equal(3,storage.StaticRebuilds);
    }
    [Fact] public void CountChangeAndMaterializedDiagnosticInputRebuildSafely()
    {
        var storage=new EnbOpenGlSkinUploadScratch();
        foreach(int count in new[]{1,8,3,0,8})
        {
            var plan=Plan(count,4,EnbW3dColorStream.White(count),Enumerable.Repeat(Vector2.Zero,count).ToImmutableArray());
            var expected=new float[count*12];plan.CopyVertexElementsTo(expected);Assert.Equal(Bits(expected),Bits(storage.Prepare(plan)));
            var explicitValues=Enumerable.Repeat(-.375f,count*12).ToImmutableArray();
            var diagnostic=new EnbOpenGlLitShaderUploadPlan(plan.LanePlan,plan.IndexedPlan,explicitValues);
            Assert.Equal(Bits(explicitValues.AsSpan()),Bits(storage.Prepare(diagnostic)));
            Assert.Equal(Bits(expected),Bits(storage.Prepare(Plan(count,4,plan.VertexColors,plan.VertexUv))));
        }
    }
    [Fact] public void BorrowedColorsAreNeverTreatedAsImmutableAndExpiredStreamsFail()
    {
        var arena=new EnbW3dTransientGeometryArena();var frame=arena.BeginFrame();
        var colors=frame.RentColors(3);colors.WriteSpan.Fill(new(1,2,3,4));
        var uv=Enumerable.Repeat(Vector2.Zero,3).ToImmutableArray();
        var plan=Plan(3,0,EnbW3dColorStream.Snapshot(colors),uv);var scratch=new EnbOpenGlSkinUploadScratch();
        _=scratch.Prepare(plan);colors.WriteSpan.Fill(new(200,100,50,255));
        var expected=new float[36];plan.CopyVertexElementsTo(expected);
        Assert.Equal(Bits(expected),Bits(scratch.Prepare(plan)));Assert.Equal(2,scratch.StaticRebuilds);
        frame.Dispose();Assert.Throws<ObjectDisposedException>(()=>{_=scratch.Prepare(plan).Length;});
    }
    [Fact] public void SteadyPreparedUploadAllocatesNoScratchPerFrame()
    {
        var storage=new EnbOpenGlSkinUploadScratch();var colors=EnbW3dColorStream.White(32);var uv=Enumerable.Repeat(Vector2.Zero,32).ToImmutableArray();
        var frames=Enumerable.Range(0,4).Select(i=>Plan(32,i,colors,uv)).ToArray();
        foreach(var p in frames)_=storage.Prepare(p);
        long before=GC.GetAllocatedBytesForCurrentThread();float sum=0;
        for(int i=0;i<1000;i++)sum+=storage.Prepare(frames[i%4])[0];
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);Assert.Equal(1,storage.StaticRebuilds);GC.KeepAlive(sum);
    }
    [Fact] public void MutableUvIdentityNeverSkipsChangedCoordinates()
    {
        var owner=new EnbW3dUvOwner();owner.Prepare(3,3,[]).Fill(new(.25f,.5f));
        var stream=new EnbW3dUvStream(owner);var scratch=new EnbOpenGlSkinUploadScratch();
        var plan=Plan(3,0,EnbW3dColorStream.White(3),stream);
        var first=scratch.Prepare(plan).ToArray();
        owner.Prepare(3,3,[]).Fill(new(.75f,.125f));
        var expected=new float[36];plan.CopyVertexElementsTo(expected);
        Assert.NotEqual(first[10],expected[10]);
        Assert.Equal(Bits(expected),Bits(scratch.Prepare(plan)));Assert.Equal(2,scratch.StaticRebuilds);
    }
    [Fact] public void OwnedUvRangeReusesCapacityAcrossCountChangesWithoutAllocations()
    {
        var owner=new EnbW3dUvOwner();Vector2[] pattern=[new(0,0),new(1,0),new(.5f,.866f)];
        owner.Prepare(300,3,pattern);
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++){owner.Prepare((i%99+1)*3,3,pattern);}
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);
        for(int i=0;i<owner.Count;i++)Assert.Equal(pattern[i%3],owner[i]);
    }
    [Fact] public void ZeroUvStreamsKeepUploadLengthsAcrossShrinkAndGrowth()
    {
        var owner=new EnbW3dUvOwner();owner.Prepare(24,0,[]);var first=new EnbW3dUvStream(owner);
        owner.Prepare(6,0,[]);var second=new EnbW3dUvStream(owner);
        Assert.Equal(24,first.Length);Assert.Equal(6,second.Length);
        Assert.False(first.AsSpan().SequenceEqual(second.AsSpan()));
        owner.Prepare(48,0,[]);var third=new EnbW3dUvStream(owner);
        Assert.Equal(24,first.Length);Assert.Equal(48,third.Length);
        Assert.All(third,v=>Assert.Equal(Vector2.Zero,v));
    }
}
