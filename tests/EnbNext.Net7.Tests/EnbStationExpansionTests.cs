using System.Numerics;
using System.Reflection;
using EnbNext.Client;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbStationExpansionTests
{
    [Fact]
    public void DialoguePageIdentitySurvivesOrdinarySnapshotPublication()
    {
        var gameplay=new Net7GameplayState();var world=new Net7WorldState();
        var source=new Net7TalkTreeUpdate("Practice", "kerr_default1", []);
        typeof(Net7GameplayState).GetProperty("TalkTree")!.SetValue(gameplay,source);
        var publisher=new EnbPresentationSnapshotPublisher();
        var first=publisher.Publish(world,null,null,1,gameplay:gameplay).StationConversation!.Tree;
        var second=publisher.Publish(world,null,null,2,gameplay:gameplay).StationConversation!.Tree;
        Assert.NotSame(source,first);Assert.Same(first,second);
        typeof(Net7GameplayState).GetProperty("TalkTree")!.SetValue(gameplay,source with {});
        Assert.NotSame(first,publisher.Publish(world,null,null,3,gameplay:gameplay).StationConversation!.Tree);
    }
    [Fact]
    public void SnapshotPreservesNpcFacilitiesAndIndependentRemoteAppearance()
    {
        var world=new Net7WorldState();
        var bytes=Net7PresentationWireTests.BuildAvatarData("REMOTE");
        var description=new byte[bytes.Length+4];
        Net7PresentationWireTests.WriteU32(description,0,42);bytes.CopyTo(description,4);
        world.Apply(Packet.ForOpcode(0x0061,description),0);
        world.Apply(Packet.ForOpcode(0x009E,Net7PresentationWireTests.BuildStarbaseAvatarChange(42,3,1.5f,new(4,5,6),1)),0);
        var gameplay=new Net7GameplayState();
        typeof(Net7GameplayState).GetProperty("LoungeDefinition")!.SetValue(gameplay,new Net7LoungeDefinitionUpdate(2,
            [new(3,0x80,[1,2,3,4,5])],[new(3,2,0,77)],
            [new(3,1,99,0,2,[],"NPC",world.AvatarDescriptions[42])]));
        var snapshot=new EnbPresentationSnapshotPublisher().Publish(world,null,null,0,gameplay:gameplay,activeStarbaseId:10651);
        Assert.Empty(snapshot.Objects);
        var person=Assert.Single(snapshot.StationAvatars);
        Assert.Equal("REMOTE",person.Avatar!.Skeleton);
        Assert.Equal(new Vector3(4,5,6),person.Position);
        Assert.Equal(3u,person.RoomType);
        var npc=Assert.Single(snapshot.StationDefinition!.Npcs);
        Assert.Equal("NPC",npc.Name);Assert.Equal("REMOTE",npc.Avatar.Skeleton);Assert.Equal(99u,npc.Id);
        var named=npc with {Name="",Avatar=npc.Avatar with {FirstName="Jane",LastName="Doe"}};
        Assert.Equal("Jane Doe",named.DisplayName);
        Assert.Equal("Shopkeeper",(named with {Name="Shopkeeper"}).DisplayName);
        Assert.Equal("Jane",(named with {Avatar=named.Avatar with {LastName=""}}).DisplayName);
        Assert.Equal(new EnbSceneStationFacility(3,2,0,77),Assert.Single(snapshot.StationDefinition.Facilities));
    }

    [Fact]
    public void RoomResourcesAndAdjacencyKeepWireOrderSeparateFromInternalClasses()
    {
        Assert.Equal("pbar01_h",EnbCurrentRetailStationInterior.RoomResourceName(2,1,0));
        Assert.Equal("pbar01_v",EnbCurrentRetailStationInterior.RoomResourceName(2,2,0));
        Assert.Equal("pbar013",EnbCurrentRetailStationInterior.RoomResourceName(2,3,0x80));
        Assert.Equal(3,EnbStationRoomLayout.Destination(1,1));
        Assert.Equal(2,EnbStationRoomLayout.Destination(1,3));
        foreach(var (a,b) in new (uint,uint)[]{(0,1),(1,3),(1,2)})
        {Assert.True(EnbStationRoomLayout.AreConnected(a,b));Assert.True(EnbStationRoomLayout.AreConnected(b,a));}
        Assert.False(EnbStationRoomLayout.AreConnected(0,2));
        Assert.False(EnbStationRoomLayout.AreConnected(uint.MaxValue,1));
        Assert.Equal("CAM_DOOR_BK_IN",EnbStationRooms.EntryPivot(1,0));
    }

    [Fact]
    public async Task RoomChangeSendsExactPairAndRejectsStaleOrDisconnectedRequests()
    {
        await using var client=new EnbClient(new());
        var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        typeof(EnbClient).GetField("_net7",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(client,session);
        typeof(EnbClient).GetProperty("State")!.SetValue(client,ClientLifecycleState.InWorld);
        session.World.SetLocalPlayerId(7);typeof(Net7Session).GetProperty("PlayerId")!.SetValue(session,7u);
        session.PlayerSemantics.ObserveStarbaseSet(new(10651,0,0));
        typeof(Net7GameplayState).GetProperty("LoungeDefinition")!.SetValue(session.Gameplay,new Net7LoungeDefinitionUpdate(2,
            [new(0,0,[]),new(1,0,[]),new(3,0,[])],[],[]));
        var publisher=(EnbPresentationSnapshotPublisher)typeof(EnbClient).GetField("_presentationSnapshots",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(client)!;
        publisher.Publish(session.World,null,null,0,gameplay:session.Gameplay,activeStarbaseId:10651);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);using var receiverLifetime=receiver;
        var sent=new List<byte[]>();channel.FrameSent+=(frame,_)=>{if(frame.Opcode==0x009F)sent.Add(frame.Payload.ToArray());};
        var request=new EnbStationRoomRequest(10651,session.World.PresentationHandoffVersion,1,3);
        Assert.False(await client.ChangeStationRoomAsync(request with {HandoffVersion=request.HandoffVersion+1}));
        Assert.False(await client.ChangeStationRoomAsync(request with {StarbaseId=10652}));
        Assert.False(await client.ChangeStationRoomAsync(request with {Source=0}));
        Assert.False(await client.ChangeStationRoomAsync(request with {Destination=2}));
        Assert.Empty(sent);
        Assert.True(await client.ChangeStationRoomAsync(request));
        Assert.Equal(Convert.FromHexString("070000000300000001000000"),Assert.Single(sent));
    }
}
