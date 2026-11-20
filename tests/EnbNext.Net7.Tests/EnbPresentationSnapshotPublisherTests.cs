using System.Buffers.Binary;
using System.Collections.Concurrent;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class EnbPresentationSnapshotPublisherTests
{
    [Fact]
    public void RescuePortraitCanArriveAfterTalkAndBeaconStateIsDetached()
    {
        var world=new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(7,1f,1,0,0,0,0));
        var game=new EnbNext.Net7.Gameplay.Net7GameplayState();
        var publisher=new EnbPresentationSnapshotPublisher();
        game.Apply(Packet.ForOpcode(0x54,Convert.FromBase64String(EnbStationConversationTests.RescueTalkPayload)));
        Assert.Null(publisher.Publish(world,effects:null,assets:null,clientTime:1,gameplay:game).StationConversation!.Portrait);
        var pip=world.LocalPlayerAuxData.Properties.Single(p=>p.Path=="LocalPlayerAuxData.PIPAvatarID");
        typeof(Net7AuxDataProperty).GetProperty("Value")!.SetValue(pip,-3);
        var avatar=Net7PresentationWireTests.BuildAvatarData("NPC");
        byte[] payload=new byte[avatar.Length+4];BinaryPrimitives.WriteUInt32LittleEndian(payload,unchecked((uint)-3));avatar.CopyTo(payload,4);
        world.Apply(Packet.ForOpcode(0x61,payload),2);
        var flag=world.Objects.FindFirst(7)!.AuxData!.Properties.Single(p=>p.Path=="ShipAuxData.IsRescueBeaconActive");
        typeof(Net7AuxDataProperty).GetProperty("Value")!.SetValue(flag,(byte)1);
        var active=publisher.Publish(world,effects:null,assets:null,clientTime:2,gameplay:game);
        Assert.NotNull(active.StationConversation!.Portrait);
        Assert.True(active.Objects.Single().IsRescueBeaconActive);
        var repeated=publisher.Publish(world,effects:null,assets:null,clientTime:3,gameplay:game);
        Assert.Same(active.StationConversation.Portrait,repeated.StationConversation!.Portrait);
        typeof(Net7AuxDataProperty).GetProperty("Value")!.SetValue(flag,(byte)0);
        typeof(Net7AuxDataProperty).GetProperty("Value")!.SetValue(pip,0);
        var cleared=publisher.Publish(world,effects:null,assets:null,clientTime:4,gameplay:game);
        Assert.False(cleared.Objects.Single().IsRescueBeaconActive);
        Assert.Null(cleared.StationConversation!.Portrait);
        Assert.True(active.Objects.Single().IsRescueBeaconActive);
    }
    [Fact]
    public void PublishMakesOnlyTheCompletedSnapshotVisibleAndClearUnpublishesIt()
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(7, 1f, 10, 0, 0f, 0f, 0f));
        var publisher = new EnbPresentationSnapshotPublisher();

        Assert.Null(publisher.Latest);

        EnbSceneSnapshot published = publisher.Publish(world, assets: null, clientTime: 123);

        Assert.Same(published, publisher.Latest);
        Assert.Equal(1UL, published.Version);
        Assert.Equal(123u, published.ClientTime);
        Assert.Equal(10, Assert.Single(published.Objects).BaseAsset);

        publisher.Clear();

        Assert.Null(publisher.Latest);
    }

    [Fact]
    public async Task ConcurrentReadersObserveWholeImmutableSnapshotsWhileWriterPublishes()
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(1, 1f, 1, 0, 0f, 0f, 0f));
        var publisher = new EnbPresentationSnapshotPublisher();
        publisher.Publish(world, assets: null, clientTime: 1);

        var failures = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource();
        Task[] readers = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    EnbSceneSnapshot? snapshot = publisher.Latest;
                    if (snapshot is null)
                    {
                        failures.Enqueue("Published snapshot unexpectedly became null.");
                        return;
                    }

                    EnbSceneObjectSnapshot obj = Assert.Single(snapshot.Objects);
                    if (obj.BaseAsset != unchecked((int)snapshot.ClientTime))
                    {
                        failures.Enqueue(
                            $"Observed torn publication: version={snapshot.Version}, time={snapshot.ClientTime}, baseAsset={obj.BaseAsset}.");
                        return;
                    }
                }
            }))
            .ToArray();

        for (uint value = 2; value <= 250; value++)
        {
            world.Apply(Packet.ForOpcode(0x0026, ChangeBaseAssetPayload(1, checked((int)value))), 0);
            publisher.Publish(world, assets: null, clientTime: value);
        }

        stop.Cancel();
        await Task.WhenAll(readers);

        Assert.Empty(failures);
        EnbSceneSnapshot latest = Assert.IsType<EnbSceneSnapshot>(publisher.Latest);
        Assert.Equal(250u, latest.ClientTime);
        Assert.Equal(250, Assert.Single(latest.Objects).BaseAsset);
        Assert.Equal(250UL, latest.Version);
    }

    private static byte[] ChangeBaseAssetPayload(uint gameId, int baseAsset)
    {
        byte[] payload = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), gameId);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), baseAsset);
        WriteSingle(payload, 8, 1f);
        WriteSingle(payload, 12, 0f);
        WriteSingle(payload, 16, 0f);
        WriteSingle(payload, 20, 0f);
        return payload;
    }

    private static void WriteSingle(byte[] destination, int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(
            destination.AsSpan(offset, 4),
            BitConverter.SingleToInt32Bits(value));
}
