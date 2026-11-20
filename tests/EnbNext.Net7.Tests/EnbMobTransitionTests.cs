using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbMobTransitionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BatchedDeathRetainsPacketSourceWithoutPublishingIntermediateFrames(bool capture, bool reuse)
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        session.World.MaterializeCreate(new(42, 1, 130, 0, 0, 0, 0));
        byte[] position = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(position, 42);
        BinaryPrimitives.WriteSingleLittleEndian(position.AsSpan(4), 125);
        BinaryPrimitives.WriteSingleLittleEndian(position.AsSpan(28), 1);
        session.World.Apply(Packet.ForOpcode(0x40, position), 0);
        var publisher = new EnbPresentationSnapshotPublisher();
        int publications = 0;
        session.PresentationStateChanged += time =>
        {
            publications++;
            publisher.Publish(session.World, session.Gameplay.Effects, null, time);
        };
        if (capture) session.PresentationStateDeferred += time =>
            publisher.CaptureDeferredEffects(session.World, session.Gameplay.Effects, null, time);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var boundary = typeof(Net7Session).GetMethod("SetPresentationBatch", flags)!.CreateDelegate<Action<bool>>(session);
        var gameplay = typeof(Net7Session).GetMethod("ApplyGameplayAndWorldAndPublish", flags)!;
        var world = typeof(Net7Session).GetMethod("ApplyWorldAndPublish", flags)!;
        boundary(true);
        try
        {
            byte[] effect = new byte[17];
            effect[0] = 7;
            BinaryPrimitives.WriteUInt32LittleEndian(effect.AsSpan(1), 42);
            BinaryPrimitives.WriteUInt16LittleEndian(effect.AsSpan(5), 397);
            BinaryPrimitives.WriteUInt32LittleEndian(effect.AsSpan(7), 9);
            BinaryPrimitives.WriteInt16LittleEndian(effect.AsSpan(15), 4000);
            gameplay.Invoke(session, [Packet.ForOpcode(9, effect), 10u]);
            BinaryPrimitives.WriteSingleLittleEndian(position.AsSpan(4), 250);
            world.Invoke(session, [Packet.ForOpcode(0x40, position), 20u]);
            world.Invoke(session, [Packet.ForOpcode(7, BitConverter.GetBytes(42u)), 30u]);
            if (reuse)
            {
                session.World.MaterializeCreate(new(42, 1, 131, 0, 0, 0, 0));
                BinaryPrimitives.WriteSingleLittleEndian(position.AsSpan(4), 999);
                world.Invoke(session, [Packet.ForOpcode(0x40, position), 40u]);
            }
            Assert.Equal(0, publications);
            Assert.Null(publisher.Latest);
        }
        finally { boundary(false); }
        Assert.Equal(1, publications);
        var retained = Assert.Single(publisher.Latest!.Effects);
        if (!capture)
        {
            Assert.Null(retained.InitialSourceObject);
            Assert.Null(retained.LastSourceObject);
            return; // Explicit positive control reproduces the original loss.
        }
        Assert.Equal(125, retained.InitialSourceObject!.Spatial!.Transform.Position.X);
        Assert.Equal(250, retained.LastSourceObject!.Spatial!.Transform.Position.X);
        Assert.Equal(retained.EndpointReceipt!.SourceCreationOrdinal, retained.InitialSourceObject.WorldCreationOrdinal);
        if (reuse) Assert.NotEqual(retained.InitialSourceObject.RenderIdentity, Assert.Single(publisher.Latest.Objects).RenderIdentity);
        else Assert.Empty(publisher.Latest.Objects);
        var catalog = new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-397]\nEffectClass=RObjEffect\nBaseAssetID=130\nDuration=4\nTrackSource=1");
        var scheduler = new EnbCurrentRetailEffectRslSchedulerRuntime();
        var frame = scheduler.Update(publisher.Latest.Effects, catalog, 50, new HashSet<uint>());
        var bindings = new EffectSourceOwnerFixture();
        bindings.Update(frame.VisualNodes, publisher.Latest.Objects);
        Assert.Equal(retained.LastSourceObject, bindings.Resolve(Assert.Single(frame.VisualNodes), publisher.Latest.Objects));
    }

    [Fact]
    public void EffectRetainsRemovedMobEvenWhenRendererMissesItsLastFrames()
    {
        var world=new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(42,1,130,0,0,0,0));
        byte[] fixedPosition=new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(fixedPosition,42);
        BinaryPrimitives.WriteSingleLittleEndian(fixedPosition.AsSpan(4),125);
        BinaryPrimitives.WriteSingleLittleEndian(fixedPosition.AsSpan(28),1);
        world.Apply(Packet.ForOpcode(0x0040,fixedPosition),0);
        var effects=new Dictionary<uint,Net7EffectState> { [9]=new(9,42,42,5,0,0,"",0,false,InstanceGeneration:1) };
        var builder=new EnbPresentationSnapshotBuilder();
        var initial=builder.Build(world,effects,null,0);
        var source=Assert.Single(initial.Objects);
        world.MaterializeRemove(42);
        var removed=builder.Build(world,effects,null,20);
        Assert.Empty(removed.Objects);
        Assert.Equal(source,Assert.Single(removed.Effects).LastSourceObject);
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-5]\nEffectClass=RObjEffect\nBaseAssetID=130\nDuration=1\nSFX=small_explosion_01.wav");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var bindings=new EffectSourceOwnerFixture();
        var frame=scheduler.Update(removed.Effects,catalog,20,new HashSet<uint>());
        var node=Assert.Single(frame.VisualNodes);
        bindings.Update(frame.VisualNodes,removed.Objects);
        Assert.Equal(source,bindings.Resolve(node,removed.Objects));
        Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(Assert.Single(frame.SoundNodes),_=>null,out var point,out var follows,out _));
        Assert.Equal(new Vector3(125,0,0),point); Assert.False(follows);
        world.MaterializeCreate(new Net7CreateUpdate(42,1,131,0x19,0,0,0));
        world.Apply(Packet.ForOpcode(0x0040,fixedPosition),30);
        var recycled=builder.Build(world,effects,null,30);
        frame=scheduler.Update(recycled.Effects,catalog,30,new HashSet<uint>{42});
        bindings.Update(frame.VisualNodes,recycled.Objects);
        Assert.Equal(source,bindings.Resolve(Assert.Single(frame.VisualNodes),recycled.Objects));
        Assert.NotEqual(source.RenderIdentity,Assert.Single(recycled.Objects).RenderIdentity);
        Assert.Empty(scheduler.Update(recycled.Effects,catalog,1020,new HashSet<uint>{42}).VisualNodes);
        Assert.Empty(builder.Build(world,new Dictionary<uint,Net7EffectState>(),null,1020).Effects);
    }

    [Fact]
    public async Task ServerTargetChangeReplacesLocalMobSelectionAndDoesNotReplayOnUnrelatedAux()
    {
        await using var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        session.World.SetLocalPlayerId(1);
        session.World.MaterializeCreate(new Net7CreateUpdate(1,1,0,1,0,0,0));
        session.World.MaterializeCreate(new Net7CreateUpdate(42,1,130,0,0,0,0));
        session.World.MaterializeCreate(new Net7CreateUpdate(43,1,130,0x19,0,0,0));
        var apply=typeof(Net7Session).GetMethod("ApplyWorldAndPublish",BindingFlags.Instance|BindingFlags.NonPublic)!;
        Net7WorldApplyResult Receive(int property,int value)=>(Net7WorldApplyResult)apply.Invoke(session,[Aux(property,value),0u])!;
        Receive(34,42); session.Gameplay.SetLocalSelectedTarget(42);
        session.World.MaterializeRemove(42);
        var result=Receive(34,43);
        Assert.Equal(new Net7TargetSelectionChange(42,43),result.TargetSelectionChange);
        Assert.Equal(43u,session.Gameplay.LocalSelectedTargetId);
        Assert.Equal(43u,session.World.CurrentTargetId);
        session.Gameplay.SetLocalSelectedTarget(99);
        Assert.Null(Receive(33,5).TargetSelectionChange);
        Assert.Equal(99u,session.Gameplay.LocalSelectedTargetId);
        Assert.Null(Receive(34,43).TargetSelectionChange);
        Assert.Equal(99u,session.Gameplay.LocalSelectedTargetId);
        Assert.Equal(new Net7TargetSelectionChange(99,null),Receive(34,0).TargetSelectionChange);
        Assert.Null(session.Gameplay.LocalSelectedTargetId);
    }

    private static Packet Aux(int property,int value)
    {
        byte[] bytes=new byte[27];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes,1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4),21);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(7),1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(11),property);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(15),value);
        return Packet.ForOpcode(0x001B,bytes);
    }
}
