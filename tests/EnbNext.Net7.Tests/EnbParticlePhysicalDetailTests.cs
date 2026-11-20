using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbParticlePhysicalDetailTests
{
    private static EnbCurrentRetailParticleScalarProperty Scalar(float v) => new(0,[new(0,v)]);
    private static EnbCurrentRetailParticleEmitterDefinition Definition => new(0,"jet","jet.tga",.2f,50,0,
        Vector3.Zero,Vector3.Zero,0,0,0,1,new(0,Vector3.Zero),new(0,Vector3.Zero),0,0,
        new(3,0,0,1,0,0,0,1,1,0,0,0,0,0,0,0),2,0,
        new(Vector3.Zero,[new(0,255,255,255,0),new(.2f,0,0,0,0)]),Scalar(1),Scalar(1),Scalar(0),0,Scalar(0))
        { LinePropertyWords=[0x02000001,0,0,0x3fc00000,0x3dcccccd,0,0,0,0,0,0,0,0,0,0,0] };
    private static EnbCurrentRetailParticleBirth Birth(int slot,float x,long run=1) =>
        new(10,1000+slot,Matrix4x4.CreateTranslation(x,0,100)) { NativeBufferSlot=slot,EmissionRun=run };

    [Fact]
    public void RetainedAdmissionPreservesAllSlotReceiptAndSelectedGeometry()
    {
        var births=Enumerable.Range(0,16).Select(i=>Birth(i,i) with {
            NativeBufferPosition=new Vector3(i,0,100),
            NativePropertyOffsets=new EnbRetailParticleBufferProperties.Offsets(Vector3.Zero,0,0,0,0,0,0)
        }).ToArray();
        foreach(int mode in new[]{0,1,2})for(int level=0;level<=16;level++)
        {
            var definition=Definition.Copy().SetRenderMode((uint)mode);
            var actual=Draw(births,level,null,out var receipt,definition);
            var selected=births.Where(b=>EnbParticleBufferDetailBudgetSemantics.IncludesPhysicalSlot(b.NativeBufferSlot!.Value,level)).ToArray();
            Assert.Equal(16,receipt.LiveParticleCount);
            if(selected.Length==0){Assert.Null(actual);continue;}
            var expected=Draw(selected,0,null,out _,definition);
            Assert.Equal(expected?.Mesh.Positions.ToArray(),actual?.Mesh.Positions.ToArray());
            Assert.Equal(expected?.Mesh.Indices,actual?.Mesh.Indices);
        }
    }

    [Fact]
    public void EveryThresholdUsesPhysicalSlotPermutationAcrossWrap()
    {
        int[] order=[11,3,7,14,0,13,1,2,5,12,15,6,9,8,4,10];
        for(int threshold=0;threshold<=16;threshold++)
        {
            Assert.Equal(16-threshold,Enumerable.Range(0,16).Count(i=>EnbParticleBufferDetailBudgetSemantics.IncludesPhysicalSlot(i,threshold)));
            for(int slot=0;slot<48;slot++)
                Assert.Equal(order[slot&15]>=threshold,EnbParticleBufferDetailBudgetSemantics.IncludesPhysicalSlot(slot,threshold));
        }
    }

    [Fact]
    public void FilteringPrecedesRunGroupingAndEndpointSelection()
    {
        // Slot4 is dropped; its different run must not split the retained same-run line.
        var births=new[]{Birth(3,-3),Birth(4,200,2),Birth(5,-1)};
        var filtered=Draw(births,12,1,out var receipt);
        var expected=Draw([births[0],births[2]],0,1,out _);
        Assert.Equal(3,receipt.LiveParticleCount);
        Assert.Equal(expected!.Mesh.Positions.ToArray(),filtered!.Mesh.Positions.ToArray());
        Assert.Equal(expected.Mesh.Indices,filtered.Mesh.Indices);
        // The original newest run is excluded: endpoint follows the last retained run.
        filtered=Draw([Birth(3,-3),Birth(5,-1),Birth(4,200,2)],12,1,out _);
        Assert.Equal(expected.Mesh.Positions.ToArray(),filtered!.Mesh.Positions.ToArray());
        Assert.Null(Draw(births,16,1,out receipt));
        Assert.Equal(3,receipt.LiveParticleCount);
    }

    [Fact]
    public void RetainedDifferentRunsRemainDisconnected()
    {
        var births=new[]{Birth(3,-3),Birth(5,-1),Birth(9,200,2),Birth(10,201,2)};
        var filtered=Draw(births,12,null,out _);
        var expected=Draw(births,0,null,out _);
        Assert.Equal(expected!.Mesh.Positions.ToArray(),filtered!.Mesh.Positions.ToArray());
        Assert.Equal(expected.Mesh.Indices,filtered.Mesh.Indices);
    }

    [Fact]
    public void SpriteSelectionUsesSlotsRatherThanBirthOrdinalOrCompactedIndex()
    {
        var births=new[]{Birth(15,-3),Birth(0,-1),Birth(1,2),Birth(3,4)};
        var filtered=Draw(births,11,null,out var receipt,Definition.Copy().SetRenderMode(0));
        var expected=Draw([births[1],births[3]],0,null,out _,Definition.Copy().SetRenderMode(0));
        Assert.Equal(4,receipt.LiveParticleCount);
        Assert.Equal(expected!.Mesh.Positions.ToArray(),filtered!.Mesh.Positions.ToArray());
        Assert.Equal(expected.Mesh.Indices,filtered.Mesh.Indices);
    }

    [Fact]
    public void OptInSelectionRejectsMissingPhysicalSlotWithoutOrdinalFallback()
    {
        var births=new[]{Birth(3,-3) with {NativeBufferSlot=null},Birth(5,-1)};
        Assert.NotNull(Draw(births,0,1,out _));
        Assert.Null(Draw(births,12,1,out _,expectedSuccess:false));
    }

    [Fact]
    public void EmitterCallbackQueuesExpiredBirthsWithoutAdvancingPhysicalWriteSlot()
    {
        var definition=Definition;
        var state=EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(definition);
        var properties=EnbRetailParticleBufferProperties.Construct(definition,
            EnbRetailParticleEmissionArithmetic.Capacity(definition),new(0x1105));
        var buffer=new EnbRetailParticleBufferRuntime(properties,200,0,Vector3.Zero);
        var queued=new List<EnbCurrentRetailParticleBirth>();
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,definition,0,0,Matrix4x4.Identity,true,1,true);
        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(state,definition,1000,1000,Matrix4x4.Identity,true,1,
            enqueue:b=>{queued.Add(b);buffer.Enqueue(b,Vector3.Zero,Vector3.Zero);});
        Assert.Contains(queued,b=>1000-b.Time>=200);
        Assert.Equal(state.EmittedCount,queued.Count);
        buffer.Advance(1000);
        Assert.Equal(queued.Where(b=>1000-b.Time<200).Select(b=>b.Ordinal),buffer.LiveBirths.Select(b=>b.Ordinal));
        Assert.Equal(Enumerable.Range(0,buffer.LiveBirths.Count).Select(i=>(int?)i),buffer.LiveBirths.Select(b=>b.NativeBufferSlot));
        Assert.True(buffer.LiveBirths[0].Ordinal>0);
    }

    [Fact]
    public void BufferPublishesActualSlotsAfterExpiredQueuedBirthAndWrap()
    {
        var properties=new EnbRetailParticleBufferProperties(3,Vector3.Zero,0,0,0,0,0,0,new(0x1105));
        var buffer=new EnbRetailParticleBufferRuntime(properties,100,0,Vector3.Zero);
        buffer.Enqueue(new(0,100,Matrix4x4.Identity),Vector3.Zero,Vector3.Zero);
        buffer.Advance(200);
        for(int i=0;i<4;i++)
        {
            buffer.Enqueue(new(201+i,101+i,Matrix4x4.Identity),new(i,0,0),Vector3.Zero);
            buffer.Advance((uint)(201+i));
        }
        Assert.Equal(new int?[]{1,2,0},buffer.LiveBirths.Select(b=>b.NativeBufferSlot));
        Assert.Equal(new long[]{102,103,104},buffer.LiveBirths.Select(b=>b.Ordinal));
    }

    private static EnbW3dDrawTask? Draw(EnbCurrentRetailParticleBirth[] births,int level,long? active,
        out EnbCurrentRetailParticleEmitterReceipt receipt,EnbCurrentRetailParticleEmitterDefinition? definition=null,
        bool expectedSuccess=true)
    {
        var emitter=new EnbW3dParticleEmitterDrawInstance("jet","jet.w3d",1,EnbRenderPassKind.OrdinaryWorld,
            definition ?? Definition,new("jet.tga","fixture.mix",0,0),Matrix4x4.CreateTranslation(3,0,100),"fixture",0,0);
        EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/2,new(1,10000),out var camera);
        bool success=EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(default,1,1,emitter,camera,
            EnbW3dLightEnvironment.BlackNoLights,1,0,20,out var packet,out receipt,out var error,
            worldBirths:births,activeEmissionRun:active,internalDetailLevel:level);
        Assert.True(success==expectedSuccess,error);
        if(!expectedSuccess)Assert.Contains("physical buffer slots",error);
        return packet;
    }
}
