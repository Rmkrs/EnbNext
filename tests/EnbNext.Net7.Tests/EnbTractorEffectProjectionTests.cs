using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using EnbNext.Net7;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Presentation;
using EnbNext.Assets;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;

public sealed class EnbTractorEffectProjectionTests
{
    [Fact]
    public async Task ProspectConvertsClockAndBindsLinkedVisualsAndSound()
    {
        await using var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        const uint server=1010609578, client=16610;
        byte[] time=new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(time,client);
        BinaryPrimitives.WriteUInt32LittleEndian(time.AsSpan(4),server);
        BinaryPrimitives.WriteUInt32LittleEndian(time.AsSpan(8),server);
        session.World.Time.ApplySetClientTime(time,client);
        byte[] wire=new byte[20];
        uint[] fields=[0x4001A3BB,0x18733,0x149425,server,60000];
        for(int i=0;i<fields.Length;i++)BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(i*4),fields[i]);
        await (Task)typeof(Net7Session).GetMethod("HandleProspectExtensionAsync",BindingFlags.Instance|BindingFlags.NonPublic)!
            .Invoke(session,[new ReadOnlyMemory<byte>(wire),CancellationToken.None])!;
        var state=Assert.Single(session.Gameplay.Effects.Values);
        Assert.Equal(client,state.Timestamp);
        Assert.Equal(Net7EffectGeometryKind.ObjectToObject,state.GeometryKind);
        var effect=Assert.Single(new EnbPresentationSnapshotPublisher().Publish(session.World,session.Gameplay.Effects,null,client).Effects);
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-191]\nEffectClass=LineEffect\nDuration=INFINITE\nSFX=sfx_mining_start\nStartLinkID=794\nUnaryStartLinkToTarget=TRUE\n[EFFECT-794]\nEffectClass=LineEffect\nDuration=INFINITE\nStartLinkID=795\n[EFFECT-795]\nEffectClass=LineEffect\nDuration=INFINITE\nSFX=sfx_mining_dep");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var frame=scheduler.Update([effect],catalog,client);
        Assert.Equal(3,frame.VisualNodes.Count);
        Assert.All(frame.VisualNodes,n=>Assert.True(n.Active));
        Assert.Equal(2,frame.SoundNodes.Count);
        foreach(var sound in frame.SoundNodes)
        {
            Assert.True(EnbCurrentRetailEffectSoundRuntime.TryResolvePosition(sound,
                id=>id==fields[0]?new Vector3(1,2,3):id==fields[1]?new Vector3(101,2,3):null,
                out var position,out _,out _,listenerPosition:new Vector3(51,20,3)));
            Assert.Equal(new Vector3(51,2,3),position);
        }
        session.Gameplay.RemoveEffect(fields[2]);
        Assert.Empty(scheduler.Update([],catalog,client+1).VisualNodes);
    }
    [Theory]
    [InlineData(0u, 0xffffffffu, 0x7fffffffu)]
    [InlineData(0xffffffffu, 1u, 0x80000000u)]
    public async Task TranslatedProspectUsesRetailEffectTimeBounds(uint correction, uint timestamp, uint expected)
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        byte[] sync = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(sync.AsSpan(4), correction);
        BinaryPrimitives.WriteUInt32LittleEndian(sync.AsSpan(8), correction);
        session.World.Time.ApplySetClientTime(sync, 0);
        byte[] wire = new byte[20];
        uint[] fields = [1, 2, 3, timestamp, 60000];
        for (int i = 0; i < fields.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(i * 4), fields[i]);
        await (Task)typeof(Net7Session).GetMethod("HandleProspectExtensionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [new ReadOnlyMemory<byte>(wire), CancellationToken.None])!;
        Assert.Equal(expected, Assert.Single(session.Gameplay.Effects.Values).Timestamp);
        Assert.Equal(0, session.World.Time.DriftSampleCount);
    }

    [Theory]
    [InlineData(0x2013, 0u, 0xffffffffu, 0x7fffffffu)]
    [InlineData(0x2014, 0u, 0xffffffffu, 0x7fffffffu)]
    [InlineData(0x2013, 0xffffffffu, 1u, 0x80000000u)]
    [InlineData(0x2014, 0xffffffffu, 1u, 0x80000000u)]
    public async Task TranslatedTractorUsesRetailEffectTimeBounds(int opcode, uint correction, uint timestamp, uint expected)
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        byte[] sync = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(sync.AsSpan(4), correction);
        BinaryPrimitives.WriteUInt32LittleEndian(sync.AsSpan(8), correction);
        session.World.Time.ApplySetClientTime(sync, 0);
        var bytes = Net7GameplayWireTests.BuildTractor((ushort)opcode, "pickup", 100, 10, Vector3.Zero, timestamp: timestamp);
        await (Task)typeof(Net7Session).GetMethod("HandleTractorExtensionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [(ushort)opcode, new ReadOnlyMemory<byte>(bytes), CancellationToken.None])!;
        Assert.Equal(expected, Assert.Single(session.Gameplay.Effects.Values).Timestamp);
        Assert.Equal(timestamp, session.Gameplay.LastTractor!.Timestamp);
        Assert.Equal(0, session.World.Time.DriftSampleCount);
    }

    [Theory]
    [InlineData(0x2013)]
    [InlineData(0x2014)]
    public async Task ExtensionConvertsServerClockAndPublishesBinaryEffect(int opcode)
    {
        await using var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        const uint server=999745938, client=26763;
        byte[] time=new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(time,client);
        BinaryPrimitives.WriteUInt32LittleEndian(time.AsSpan(4),server);
        BinaryPrimitives.WriteUInt32LittleEndian(time.AsSpan(8),server);
        session.World.Time.ApplySetClientTime(time,client);
        var bytes=Net7GameplayWireTests.BuildTractor((ushort)opcode,"pickup",6628,100,new Vector3(1,2,3),timestamp:server);
        await (Task)typeof(Net7Session).GetMethod("HandleTractorExtensionAsync",BindingFlags.Instance|BindingFlags.NonPublic)!
            .Invoke(session,[(ushort)opcode,new ReadOnlyMemory<byte>(bytes),CancellationToken.None])!;
        var state=Assert.Single(session.Gameplay.Effects.Values);
        Assert.Equal(client,state.Timestamp);
        Assert.Equal(Net7EffectGeometryKind.ObjectToObject,state.GeometryKind);
        Assert.True(state.IsObjectToObject);
        Assert.Equal("TRACTOR",state.BoneName);
        Assert.Equal(server,session.Gameplay.LastTractor!.Timestamp);
        var scene=new EnbPresentationSnapshotPublisher().Publish(session.World,session.Gameplay.Effects,null,client);
        var effect=Assert.Single(scene.Effects);
        Assert.True(effect.EndWithTarget);
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("[EFFECT-2]\nEffectClass=LineEffect\nBaseAssetID=68\nDuration=INFINITE");
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        var active=Assert.Single(scheduler.Update([effect],catalog,client).VisualNodes);
        Assert.True(active.Active);
        Assert.Equal(client,active.ActualStartTick);
        Assert.Empty(scheduler.Update([effect],catalog,client+6628).VisualNodes);
        // Rendering starts after packet receipt: its finite timer must not keep
        // the native tractor alive after the temporary pickup has been removed.
        var delayed = new EnbCurrentRetailEffectRslSchedulerRuntime();
        var members = new HashSet<uint> { effect.SourceGameId, effect.TargetGameId };
        Assert.Single(delayed.Update([effect], catalog, client + 2000, members).VisualNodes);
        members.Remove(effect.TargetGameId);
        Assert.Empty(delayed.Update([effect], catalog, client + 6628, members).VisualNodes);
    }
}
