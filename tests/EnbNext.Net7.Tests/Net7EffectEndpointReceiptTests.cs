using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;
using System.Numerics;
using System.Buffers.Binary;

namespace EnbNext.Net7.Tests;

public sealed class Net7EffectEndpointReceiptTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ObjectToPointCapturesSourceLifetimeAndPublishesRetainedSource(bool initiallyPresent)
    {
        var world=new Net7WorldState();if(initiallyPresent)Add(world,SourceId);
        var gameplay=new Net7GameplayState(resolveWorldObject:world.Objects.FindFirst);
        using var payload=new MemoryStream();
        using(var writer=new BinaryWriter(payload,System.Text.Encoding.Latin1,true))
        {
            writer.Write(10u);writer.Write(0u);writer.Write(SourceId);writer.Write((byte)0);
            writer.Write(100f);writer.Write(25f);writer.Write(50f);writer.Write((short)0);writer.Write((ushort)933);
            writer.Write(1f);writer.Write(0f);writer.Write(0f);writer.Write(0f);writer.Write(1f);
        }
        var packet=Packet.ForOpcode(0xD,payload.ToArray());Assert.True(gameplay.Apply(packet));
        var receipt=Assert.Single(gameplay.Effects).Value.EndpointReceipt!;
        Assert.Equal(initiallyPresent,receipt.SourceCreationOrdinal.HasValue);Assert.Null(receipt.TargetCreationOrdinal);
        if(initiallyPresent)world.MaterializeRemove(SourceId);
        Add(world,SourceId);Move(world,SourceId,new(10,20,30));
        var builder=new EnbPresentationSnapshotBuilder();
        var late=Assert.Single(builder.Build(world,gameplay.Effects,null,100).Effects);
        Assert.Null(late.InitialSourceObject);Assert.Null(late.LastSourceObject);
        Assert.Equal(new Vector3(100,25,50),late.WorldPoint);Assert.Null(late.PublishedEndpoints);
        Assert.True(gameplay.Apply(packet));
        var initial=Assert.Single(builder.Build(world,gameplay.Effects,null,200).Effects);
        Assert.Equal(new Vector3(10,20,30),initial.InitialSourceObject!.Spatial!.Transform.Position);
        Move(world,SourceId,new(40,50,60));
        var moved=Assert.Single(builder.Build(world,gameplay.Effects,null,300).Effects);
        Assert.Equal(initial.InitialSourceObject,moved.InitialSourceObject);
        Assert.Equal(new Vector3(40,50,60),moved.LastSourceObject!.Spatial!.Transform.Position);
        world.MaterializeRemove(SourceId);
        var removed=Assert.Single(builder.Build(world,gameplay.Effects,null,400).Effects);
        Assert.Equal(moved.LastSourceObject,removed.LastSourceObject);Assert.Null(removed.PublishedEndpoints);
        Assert.False(removed.IsObjectToObject);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObjectEffectCannotCaptureLateOrRecycledOwnerBeforeFirstPublication(bool initiallyPresent)
    {
        var world=new Net7WorldState();
        if(initiallyPresent)Add(world,SourceId);
        var gameplay=new Net7GameplayState(resolveWorldObject:world.Objects.FindFirst);
        var payload=new byte[7];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1),SourceId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(5),674);
        Assert.True(gameplay.Apply(Packet.ForOpcode(9,payload)));
        var receipt=Assert.Single(gameplay.Effects).Value.EndpointReceipt!;
        Assert.Equal(initiallyPresent,receipt.SourceCreationOrdinal.HasValue);
        Assert.Null(receipt.TargetCreationOrdinal);
        if(initiallyPresent)world.MaterializeRemove(SourceId);
        Add(world,SourceId);Move(world,SourceId,new(10,20,30));
        var effect=Assert.Single(new EnbPresentationSnapshotBuilder().Build(world,gameplay.Effects,null,100).Effects);
        Assert.Null(effect.InitialSourceObject);
        Assert.Null(effect.LastSourceObject);
        Assert.Equal(receipt.SourceCreationOrdinal,effect.EndpointReceipt!.SourceCreationOrdinal);
    }

    [Fact]
    public void ObjectEffectCapturesObservedOwnerAndRetainsItAfterRemoval()
    {
        var world=new Net7WorldState();Add(world,SourceId);Move(world,SourceId,new(10,20,30));
        var gameplay=new Net7GameplayState(resolveWorldObject:world.Objects.FindFirst);
        var payload=new byte[7];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1),SourceId);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(5),674);
        Assert.True(gameplay.Apply(Packet.ForOpcode(9,payload)));
        var builder=new EnbPresentationSnapshotBuilder();
        var initial=Assert.Single(builder.Build(world,gameplay.Effects,null,100).Effects);
        Assert.Equal(new Vector3(10,20,30),initial.InitialSourceObject!.Spatial!.Transform.Position);
        Move(world,SourceId,new(40,50,60));
        var moved=Assert.Single(builder.Build(world,gameplay.Effects,null,200).Effects);
        Assert.Equal(initial.InitialSourceObject,moved.InitialSourceObject);
        Assert.Equal(new Vector3(40,50,60),moved.LastSourceObject!.Spatial!.Transform.Position);
        world.MaterializeRemove(SourceId);
        var removed=Assert.Single(builder.Build(world,gameplay.Effects,null,300).Effects);
        Assert.Equal(moved.InitialSourceObject,removed.InitialSourceObject);
        Assert.Equal(moved.LastSourceObject,removed.LastSourceObject);
    }

    [Fact]
    public void ObjectEffectWithoutResolverKeepsUnobservedReceipt()
    {
        var gameplay=new Net7GameplayState();
        Assert.True(gameplay.Apply(Packet.ForOpcode(9,new byte[7])));
        Assert.Null(Assert.Single(gameplay.Effects).Value.EndpointReceipt);
    }

    [Fact]
    public void DurationLinkedBeamCapturesMissingSourceBeforeLaterArrival()
    {
        var world=new Net7WorldState();Add(world,TargetId);
        var gameplay=new Net7GameplayState(resolveWorldObject:world.Objects.FindFirst);
        using var stream=new MemoryStream();using(var writer=new BinaryWriter(stream,System.Text.Encoding.Latin1,true))
        {
            writer.Write(1u);writer.Write(0u);writer.Write(SourceId);writer.Write((byte)0);writer.Write(TargetId);
            writer.Write((short)0);writer.Write((ushort)3);
            writer.Write(0f);writer.Write(0f);writer.Write(0f);writer.Write((short)0);writer.Write((short)0);writer.Write((byte)0);
            writer.Write(1f);writer.Write(0f);writer.Write(0f);writer.Write(0f);writer.Write(1f);
        }
        Assert.True(gameplay.Apply(Packet.ForOpcode(0xe,stream.ToArray())));
        Add(world,SourceId);
        var effect=Assert.Single(gameplay.Effects).Value;
        Assert.Null(effect.EndpointReceipt!.SourceCreationOrdinal);
        Assert.Equal(world.Objects.FindFirst(TargetId)!.CreationOrdinal,effect.EndpointReceipt.TargetCreationOrdinal);
        var published=Assert.Single(new EnbPresentationSnapshotBuilder().Build(world,gameplay.Effects,null,100).Effects);
        Assert.Null(published.EndpointReceipt!.SourceCreationOrdinal);
    }
    private const uint SourceId = 0x187B3, TargetId = 0x40011E2D;
    // Passive captured E850 000B; no endpoint substitution or live traffic.
    private static Packet Missile() => Packet.ForOpcode(0x000B,
        Convert.FromHexString("0708B38701002D1E014052030098A81400941AA13BE00A00000000"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicationsRetainLaunchAndLatestTargetAcrossRemovalAndReuse(bool durationLinked)
    {
        var world=new Net7WorldState();
        Add(world,SourceId);Add(world,TargetId);
        Move(world,SourceId,new(10,20,30));Move(world,TargetId,new(100,200,300));
        var game=new Net7GameplayState(resolveWorldObject:world.Objects.FindFirst);
        Packet packet=durationLinked?Beam():Missile();
        game.Apply(packet);
        var publisher=new EnbPresentationSnapshotPublisher();
        var first=Assert.Single(publisher.Publish(world,game.Effects,null,0).Effects).PublishedEndpoints!;
        Assert.NotNull(first);
        Assert.True(first.CompleteAtFirstPublication);
        Move(world,SourceId,new(40,50,60));Move(world,TargetId,new(400,500,600));
        var moved=Assert.Single(publisher.Publish(world,game.Effects,null,0).Effects).PublishedEndpoints!;
        Assert.Equal(new Vector3(10,20,30),moved.Source.Spatial!.Transform.Position);
        Assert.Equal(new Vector3(400,500,600),moved.Target.Spatial!.Transform.Position);
        Assert.Equal(new Vector3(100,200,300),moved.InitialTarget!.Spatial!.Transform.Position);
        Assert.Equal(new Vector3(100,200,300),first.Target.Spatial!.Transform.Position);
        world.MaterializeRemove(SourceId);world.MaterializeRemove(TargetId);
        var absent=publisher.Publish(world,game.Effects,null,0);
        Assert.Empty(absent.Objects);
        Assert.Equal(moved,Assert.Single(absent.Effects).PublishedEndpoints);
        var soundRoot=Assert.Single(absent.Effects);
        if(durationLinked)
        {
            var beamDefinition=EnbR10EffectSoundTests.Catalog("[EFFECT-3]\nEffectClass=RObjLookAtEffect\nSourceBased=0\nSFX=x.wav\nDuration=.5").ResolveEffect(3).Definition!;
            var beamSound=new EnbNext.Rendering.EnbCurrentRetailEffectSoundNode(1,beamDefinition,soundRoot,TargetId,
                EnbNext.Rendering.EnbCurrentRetailEffectLinkKind.Root,true,0);
            Assert.True(EnbNext.Rendering.EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(beamSound,_=>null,out var position,out _,out _));
            Assert.Equal(new Vector3(400,500,600),position);
            Assert.Equal(new Vector3(40,50,60),soundRoot.LastSourceObject!.Spatial!.Transform.Position);
        }
        var soundDefinition=EnbR10EffectSoundTests.Catalog("[EFFECT-91]\nEffectClass=RObjEffect\nSFX=explosive_explosion_effect_bigger.wav\nDuration=1").ResolveEffect(91).Definition!;
        var sound=new EnbNext.Rendering.EnbCurrentRetailEffectSoundNode(1,soundDefinition,soundRoot,TargetId,
            EnbNext.Rendering.EnbCurrentRetailEffectLinkKind.NextLink,true,0);
        // Linked RObj audio now consumes the production bound matrix. A retained
        // endpoint alone cannot distinguish an object link from a seek matrix copy.
        Assert.False(EnbNext.Rendering.EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound,_=>null,out _,out _,out _));
        Assert.True(EnbNext.Rendering.EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound,_=>null,out var soundPosition,out _,out _,
            findEffectPosition:_=>moved.Target.Spatial!.Transform.Position));
        Assert.Equal(new Vector3(400,500,600),soundPosition);
        Assert.False(EnbNext.Rendering.EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound with {Root=soundRoot with {PublishedEndpoints=null}},_=>null,out _,out _,out _));
        Add(world,SourceId);Add(world,TargetId);
        Move(world,SourceId,new(700,800,900));Move(world,TargetId,new(1000,1100,1200));
        Assert.Equal(moved,Assert.Single(publisher.Publish(world,game.Effects,null,0).Effects).PublishedEndpoints);
        game.Apply(packet);
        var replacement=Assert.Single(publisher.Publish(world,game.Effects,null,0).Effects).PublishedEndpoints!;
        Assert.NotEqual(moved.Source.RenderIdentity,replacement.Source.RenderIdentity);
        Assert.Equal(new Vector3(700,800,900),replacement.Source.Spatial!.Transform.Position);
        publisher.Publish(world,new Dictionary<uint,Net7EffectState>(),null,0);
        // Republishing an old generation cannot recover its pruned snapshot from a replacement.
        var oldEffect=Assert.Single(absent.Effects);
        var oldState=Assert.Single(game.Effects).Value with {InstanceGeneration=oldEffect.InstanceGeneration,
            EndpointReceipt=new(first.Source.WorldCreationOrdinal,first.Target.WorldCreationOrdinal)};
        Assert.Null(Assert.Single(publisher.Publish(world,new Dictionary<uint,Net7EffectState>{{oldState.EffectId,oldState}},null,0).Effects).PublishedEndpoints);
    }

    private static Packet Beam()
    {
        using var stream=new MemoryStream();
        using(var writer=new BinaryWriter(stream,System.Text.Encoding.Latin1,true))
        {
            writer.Write(1u);writer.Write(0u);writer.Write(SourceId);writer.Write((byte)0);writer.Write(TargetId);
            writer.Write((short)0);writer.Write((ushort)3);
            writer.Write(0f);writer.Write(0f);writer.Write(0f);writer.Write((short)0);writer.Write((short)0);writer.Write((byte)0);
            writer.Write(1f);writer.Write(0f);writer.Write(0f);writer.Write(0f);writer.Write(1f);
        }
        return Packet.ForOpcode(0xe,stream.ToArray());
    }

    [Fact]
    public void NullProviderEndpointsPublishAtCreationAndRecoverOnlyWithOriginalObjects()
    {
        var world=new Net7WorldState();
        Add(world,SourceId);Add(world,TargetId);
        var game=new Net7GameplayState(resolveWorldObject:world.Objects.FindFirst);
        game.Apply(Missile());
        var publisher=new EnbPresentationSnapshotPublisher();
        // Before any positional packet both objects own 6F2820's null provider
        // (00B006B8), so 006E7770 captures them at the origin.
        var first=Assert.Single(publisher.Publish(world,game.Effects,null,0).Effects).PublishedEndpoints!;
        Assert.True(first.CompleteAtFirstPublication);
        Assert.Equal(EnbSceneSpatialProviderKind.Null,first.Source.Spatial!.ProviderKind);
        Assert.Equal(Vector3.Zero,first.Target.Spatial!.Transform.Position);
        Move(world,SourceId,new(10,20,30));Move(world,TargetId,new(100,200,300));
        var moved=Assert.Single(publisher.Publish(world,game.Effects,null,0).Effects).PublishedEndpoints!;
        Assert.True(moved.CompleteAtFirstPublication);
        Assert.Equal(new Vector3(100,200,300),moved.Target.Spatial!.Transform.Position);
        var freshPublisher=new EnbPresentationSnapshotPublisher();
        world.MaterializeRemove(SourceId);Add(world,SourceId);Move(world,SourceId,new(40,50,60));
        Assert.Null(Assert.Single(freshPublisher.Publish(world,game.Effects,null,0).Effects).PublishedEndpoints);
    }

    private static void Move(Net7WorldState world,uint id,Vector3 position)
    {
        var payload=new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(payload,id);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(4),position.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(8),position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(12),position.Z);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(28),1);
        world.Apply(Packet.ForOpcode(0x0040,payload),0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PacketReceiptSurvivesLateArrivalRemovalAndGameIdReuse(bool sourcePresent, bool targetPresent)
    {
        var world = new Net7WorldState();
        if (sourcePresent) Add(world, SourceId);
        if (targetPresent) Add(world, TargetId);
        ulong? sourceOrdinal = world.Objects.FindFirst(SourceId)?.CreationOrdinal;
        ulong? targetOrdinal = world.Objects.FindFirst(TargetId)?.CreationOrdinal;
        var gameplay = new Net7GameplayState(resolveWorldObject: world.Objects.FindFirst);
        Assert.True(gameplay.Apply(Missile()));
        var effect = Assert.Single(gameplay.Effects).Value;
        Assert.Equal(new Net7EffectEndpointReceipt(sourceOrdinal, targetOrdinal), effect.EndpointReceipt);

        // Change membership before the first presentation frame. Existing objects
        // have no spatial state or assets: those are not lookup prerequisites.
        world.MaterializeRemove(SourceId);
        world.MaterializeRemove(TargetId);
        Add(world, SourceId);
        Add(world, TargetId);
        var builder = new EnbPresentationSnapshotBuilder();
        var sceneEffect = Assert.Single(builder.Build(world, gameplay.Effects, null, 100).Effects);
        Assert.Equal(new EnbSceneEffectEndpointReceipt(sourceOrdinal, targetOrdinal), sceneEffect.EndpointReceipt);
        Assert.Equal(effect.EndpointReceipt, Assert.Single(gameplay.Effects).Value.EndpointReceipt);

        // A new packet using the same effect ID gets a fresh observation/generation.
        Assert.True(gameplay.Apply(Missile()));
        var replacement = Assert.Single(gameplay.Effects).Value;
        Assert.True(replacement.InstanceGeneration > effect.InstanceGeneration);
        Assert.Equal(world.Objects.FindFirst(SourceId)!.CreationOrdinal, replacement.EndpointReceipt!.SourceCreationOrdinal);
        Assert.Equal(world.Objects.FindFirst(TargetId)!.CreationOrdinal, replacement.EndpointReceipt.TargetCreationOrdinal);
        Assert.NotEqual(effect.EndpointReceipt, replacement.EndpointReceipt);
    }

    [Fact]
    public void MissingResolverIsUnobservedRatherThanTwoAbsentObjects()
    {
        var gameplay = new Net7GameplayState();
        Assert.True(gameplay.Apply(Missile()));
        Assert.Null(Assert.Single(gameplay.Effects).Value.EndpointReceipt);
        var scene = new EnbPresentationSnapshotBuilder().Build(new Net7WorldState(), gameplay.Effects, null, 100);
        Assert.Null(Assert.Single(scene.Effects).EndpointReceipt);
    }

    [Fact]
    public void SyntheticEffectsDoNotInventPacketObservations()
    {
        var gameplay = new Net7GameplayState(resolveWorldObject: _ => null);
        gameplay.AddSyntheticEffect(new Net7EffectState(1, SourceId, TargetId, 850, 0, 2784, "", 0, true));
        Assert.Null(Assert.Single(gameplay.Effects).Value.EndpointReceipt);
    }

    private static void Add(Net7WorldState world, uint id) =>
        world.MaterializeCreate(new Net7CreateUpdate(id, 1, 69, 2, 0, 0, 0));
}
