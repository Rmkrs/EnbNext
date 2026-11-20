using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbSharedParticleRandomnessTests
{
    private static EnbCurrentRetailParticleScalarProperty Scalar(float value,float random=0)=>new(random,[new(0,value)]);
    private static EnbCurrentRetailParticleEmitterDefinition Definition=>new(0,"shared","smoke.tga",1,10,0,
        new(2,3,4),Vector3.Zero,0,0,0,1,new(0,new(1,2,3)),new(0,new(4,5,6)),0,0,
        new(3,0,0,1,0,0,0,1,1,0,0,0,0,0,0,0),0,0,
        new(new(.1f,.2f,.3f),[new(0,120,130,140,0)]),Scalar(1,.2f),Scalar(1,.3f),Scalar(0,.1f),.2f,Scalar(0,.1f));

    [Fact]
    public void OwnersInterleaveNativeConstructionBirthsAndLineReservationsWithoutReseeding()
    {
        var d=Definition;var streams=new EnbRetailParticleRandomStreams();var reference=new EnbRetailParticleRandomStreams();
        var first=new EnbRetailParticleRandomOwner(d,streams);
        var firstProperties=EnbRetailParticleBufferProperties.Construct(d,EnbRetailParticleEmissionArithmetic.Capacity(d),reference.Properties);
        var second=new EnbRetailParticleRandomOwner(d,streams);
        var secondProperties=EnbRetailParticleBufferProperties.Construct(d,EnbRetailParticleEmissionArithmetic.Capacity(d),reference.Properties);
        Assert.Equal(reference.Properties.WordsConsumed,streams.Properties.WordsConsumed);
        Assert.Equal(firstProperties.AtSlot(0),first.Properties.AtSlot(0));
        Assert.Equal(secondProperties.AtSlot(0),second.Properties.AtSlot(0));
        Assert.NotEqual(first.Properties.AtSlot(0),second.Properties.AtSlot(0));
        var motion=EnbRetailParticleEmitterMotion.Construct(d);
        foreach(var owner in new[]{first,second,first})
        {
            var actual=owner.Sample(new(100,0,Matrix4x4.Identity));
            var expected=EnbRetailParticleBirthSampler.Sample(motion.CreationRandomizer,motion.VelocityRandomizer,
                motion.ConstantVelocityPerMillisecond,motion.OutwardVelocityPerMillisecond,false,
                Quaternion.Identity,Vector3.Zero,Vector3.Zero,reference.Births);
            Assert.Equal(expected.Position,actual.SampledPosition);
            Assert.Equal(expected.VelocityPerMillisecond,actual.SampledVelocityPerMillisecond);
            Assert.Equal(reference.Births.Reserve(9),streams.ReserveLineWords(9));
        }
        Assert.Equal(reference.Births.Counter,streams.Births.Counter);
    }

    [Fact]
    public void CloneCopiesTablesWithoutDrawingAndOwnsIndependentMutableTables()
    {
        var streams=new EnbRetailParticleRandomStreams();var owner=new EnbRetailParticleRandomOwner(Definition,streams);
        ulong words=streams.Properties.WordsConsumed;uint births=streams.Births.Counter;
        var clone=owner.Clone();
        for(int i=0;i<owner.Properties.Capacity;i++)Assert.Equal(owner.Properties.AtSlot(i),clone.Properties.AtSlot(i));
        clone.Properties.ScaleSize(2);
        Assert.NotEqual(owner.Properties.AtSlot(0).Size,clone.Properties.AtSlot(0).Size);
        Assert.Equal(words,streams.Properties.WordsConsumed);Assert.Equal(births,streams.Births.Counter);
    }


    [Fact]
    public void RuntimeColorSetterConsumesItsRebuiltTableAfterAllConstructorTables()
    {
        var original=Definition;var shifted=original.Copy().SetColor(original.Color with {BeforeRuntimeSetter=original.Color});
        var actual=new EnbRetailParticleRandomStreams();var expected=new EnbRetailParticleRandomStreams();
        var owner=new EnbRetailParticleRandomOwner(shifted,actual);
        var properties=EnbRetailParticleBufferProperties.Construct(original,EnbRetailParticleEmissionArithmetic.Capacity(original),expected.Properties);
        properties.SetColor(shifted.Color.Random,expected.Properties);
        Assert.Equal(expected.Properties.WordsConsumed,actual.Properties.WordsConsumed);
        for(int i=0;i<properties.Capacity;i++)Assert.Equal(properties.AtSlot(i),owner.Properties.AtSlot(i));
    }

    [Fact]
    public void FiniteReplayConsumesExpiredBirthsOnceAndRetainsPhysicalOffsets()
    {
        var d=Definition.Copy().SetMaximumEmissions(30);var streams=new EnbRetailParticleRandomStreams();
        var replay=new EnbRetailParticleReplayHistory(d,streams);
        EnbCurrentRetailParticleBirth Birth(long ordinal)=>new(100+ordinal*100,ordinal,Matrix4x4.Identity);
        var live=replay.Prepare(20,2001,Birth).ToArray();
        Assert.Equal(120u,streams.Births.Counter);
        Assert.All(live,b=>{Assert.NotNull(b.SampledPosition);Assert.NotNull(b.NativePropertyOffsets);Assert.NotNull(b.NativeBufferSlot);});
        Assert.Equal(live,replay.Prepare(20,2001,_=>throw new Exception("redraw sampled again")));
        Assert.Equal(120u,streams.Births.Counter);
        Assert.Empty(replay.Prepare(20,4000,_=>throw new Exception("retirement sampled again")));
        Assert.Equal(120u,streams.Births.Counter);
    }

    [Fact]
    public void AdmittedLongFiniteDebrisLifetimeDoesNotTruncateToUint()
    {
        var d=Definition.Copy().SetMaximumEmissions(5).SetLifetimeSeconds(10_000_000);
        var streams=new EnbRetailParticleRandomStreams();var replay=new EnbRetailParticleReplayHistory(d,streams);
        var records=replay.Prepare(5,5_000_000_000,i=>new(100+i, i,Matrix4x4.Identity));
        Assert.Equal(5,records.Count);Assert.Equal(30u,streams.Births.Counter);
        EnbResolvedRenderCamera.TryCreate(Vector3.Zero,Quaternion.Identity,new(800,600,1),Math.PI/3,new(1,100000),out var camera);
        var emitter=new EnbW3dParticleEmitterDrawInstance(d.Name,"long.w3d",1,EnbRenderPassKind.OrdinaryWorld,d,
            new("smoke.tga","fixture.mix",0,1),Matrix4x4.Identity,"",0,0);
        Assert.True(EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(new("long.w3d","fixture.mix",0,1),1,1,
            emitter,camera,EnbW3dLightEnvironment.BlackNoLights,0,0,5_000_000_000,out var packet,out var receipt,out var error,randomHistory:replay),error);
        Assert.NotNull(packet);Assert.Equal(5,receipt.LiveParticleCount);Assert.Equal(30u,streams.Births.Counter);
        Assert.Empty(replay.Prepare(5,10_000_001_000,_=>throw new Exception("expired debris resampled")));
    }
}
