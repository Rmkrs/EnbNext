using System.Buffers.Binary;
using System.Reflection;
using System.Numerics;
using EnbNext.Client;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.Protocol;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed partial class EnbStationConversationTests
{
    [Fact]
    public async Task ShipCustomizationChecksFundsAndPreservesProfessionAndUpgrade()
    {
        await using var client=new EnbClient(new());var s=Attach(client);var owner=TerminalOwner(s,5);
        Set(s.World.LocalPlayerAuxData!,"Money",1ul);
        var publish=typeof(EnbClient).GetMethod("OnNet7PresentationStateChanged",BindingFlags.Instance|BindingFlags.NonPublic)!;
        s.PresentationStateChanged+=time=>publish.Invoke(client,[time]);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s);using var lifetime=receiver;
        var frames=new List<(ushort Op,byte[] Body)>();channel.FrameSent+=(f,_)=>frames.Add((f.Opcode,f.Payload.ToArray()));
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Open)));
        var dispatch=typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        await (Task)dispatch.Invoke(s,[Packet.ForOpcode(0x81,Net7PresentationWireTests.BuildShipRecustomization()),"offline customization",CancellationToken.None])!;
        var original=client.LatestPresentationSnapshot!.ShipCustomization!.Customization;
        var edited=original with {ShipName="New name",Profession=99,TechLevel=99};
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Customize){Ship=edited}));
        Assert.Single(frames);
        Set(s.World.LocalPlayerAuxData!,"Money",10000ul);publish.Invoke(client,[0u]);
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Customize){Ship=edited}));
        Assert.Equal((ushort)0x82,frames.Last().Op);
        Assert.Equal(Net7GameplayWire.BuildRecustomizeShipDone(Net7Recustomization.EncodeShip(edited with {Profession=original.Profession,TechLevel=original.TechLevel}),7),frames.Last().Body);
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Customize){Ship=edited}));
        Assert.Equal(2,frames.Count);
    }

    [Theory]
    [InlineData(5u,11)]
    [InlineData(6u,10)]
    public async Task CustomizationWaitsForFreshCostsAndCancelDoesNotSubmit(uint facility,byte action)
    {
        await using var client=new EnbClient(new()); var s=Attach(client);var owner=TerminalOwner(s,facility);
        s.World.Apply(Packet.ForOpcode(0x37,BitConverter.GetBytes(42u)),0);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s);using var lifetime=receiver;
        var frames=new List<(ushort Op,byte[] Body)>();channel.FrameSent+=(f,_)=>frames.Add((f.Opcode,f.Payload.ToArray()));
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Open)));
        Assert.Equal(Net7GameplayWire.BuildStarbaseRequest(7,0,action),Assert.Single(frames).Body);
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Customize){Avatar=s.World.AvatarDescriptions[42]}));
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Close)));
        Assert.Single(frames);
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Customize)));
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Open)));
        Assert.Null(s.Gameplay.AvatarRecustomizationStart);Assert.Null(s.Gameplay.ShipRecustomization);
        Assert.False(await client.StationTerminalAsync(new(owner with {HandoffVersion=owner.HandoffVersion+1},EnbTerminalCommand.Close)));
    }

    [Fact]
    public async Task AvatarTerminalWithoutLocalAppearanceOpensNothingAndSendsNothing()
    {
        // 0054BD30 dereferences FindAvatarAppearanceByObjectId (0074C960 returns 0 when
        // absent) before it queues StarbaseRequest 10: an undefined read, so nothing opens.
        await using var client=new EnbClient(new());var s=Attach(client);var owner=TerminalOwner(s,6);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s);using var lifetime=receiver;
        var frames=new List<(ushort Op,byte[] Body)>();channel.FrameSent+=(f,_)=>frames.Add((f.Opcode,f.Payload.ToArray()));
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Open)));
        Assert.Empty(frames);
        s.World.Apply(Packet.ForOpcode(0x37,BitConverter.GetBytes(42u)),0);
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Open)));
        Assert.Equal(Net7GameplayWire.BuildStarbaseRequest(7,0,10),Assert.Single(frames).Body);
    }

    [Fact]
    public async Task AvatarCustomizationPublishesPriceAndSendsOneConfirmedAppearance()
    {
        await using var client=new EnbClient(new());var s=Attach(client);var owner=TerminalOwner(s,6);
        s.World.Apply(Packet.ForOpcode(0x37,BitConverter.GetBytes(42u)),0);
        Set(s.World.LocalPlayerAuxData!,"Money",10000ul);
        var publish=typeof(EnbClient).GetMethod("OnNet7PresentationStateChanged",BindingFlags.Instance|BindingFlags.NonPublic)!;
        s.PresentationStateChanged+=time=>publish.Invoke(client,[time]);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s);using var lifetime=receiver;
        var frames=new List<(ushort Op,byte[] Body)>();channel.FrameSent+=(f,_)=>frames.Add((f.Opcode,f.Payload.ToArray()));
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Open)));
        var original=s.World.AvatarDescriptions[42];var edited=original with {HairNum=(byte)(original.HairNum+1)};
        byte[] costs=new byte[60];for(int i=0;i<14;i++)BinaryPrimitives.WriteUInt32LittleEndian(costs.AsSpan(i*4),100);
        var dispatch=typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        await (Task)dispatch.Invoke(s,[Packet.ForOpcode(0x83,costs),"offline customization",CancellationToken.None])!;
        Assert.NotNull(client.LatestPresentationSnapshot!.AvatarCustomization);
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Customize){Avatar=original}));
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Customize){Avatar=edited}));
        Assert.Equal(Net7GameplayWire.BuildRecustomizeAvatarDone(Net7AvatarCreateCodec.EncodeRecustomizationData(edited),7),frames.Last().Body);
        Assert.Equal((ushort)0x84,frames.Last().Op);Assert.Equal(edited,s.World.AvatarDescriptions[42]);
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Customize){Avatar=edited}));
        Assert.Equal(2,frames.Count);
    }
}

public sealed class Net7RecustomizationTests
{
    [Fact]
    public void AvatarPricesChangedGroupsOnceAndKeepsMaterialRules()
    {
        var (a,_)=Net7AvatarRosterCodec.DecodeAvatarData(Net7PresentationWireTests.BuildAvatarData(""),"fixture");
        uint[] costs=Enumerable.Range(1,14).Select(x=>(uint)x*10).ToArray();
        Assert.Equal(0,Net7Recustomization.AvatarCost(a,a,costs));
        var b=a with {HairNum=(byte)(a.HairNum+1),EyeColor=[.1f,.2f,.3f]};
        Assert.Equal(60,Net7Recustomization.AvatarCost(a,b,costs));
        var metal=a with {ShirtPrimaryMetal=1};
        Assert.Equal(0,Net7Recustomization.AvatarCost(metal,metal with {ShirtPrimaryColor=[.1f,.2f,.3f]},costs));
        Assert.Equal(100,Net7Recustomization.AvatarCost(metal,metal with {ShirtPrimaryMetal=2},costs));
    }

    [Fact]
    public void ShipSerializerAndCostOrderPreserveUntouchedEntries()
    {
        var start=Net7PresentationWire.DecodeShipRecustomizeStart(Net7PresentationWireTests.BuildShipRecustomization());
        Assert.Equal(start.Customization.RawPayload,Net7Recustomization.EncodeShip(start.Customization));
        var p=start.Customization.PartAttributes.ToArray();p[4]=p[4] with {Color=Vector3.Zero};
        var edited=start.Customization with {PartAttributes=p};
        Assert.Equal((int)start.AuxiliaryDwords[4],Net7Recustomization.ShipCost(start.Customization,edited,start.AuxiliaryDwords));
        var wire=Net7Recustomization.EncodeShip(edited);
        Assert.Equal(start.Customization.RawPayload.AsSpan(0,58).ToArray(),wire.AsSpan(0,58).ToArray());
        Assert.Equal(start.Customization.RawPayload.AsSpan(58+5*17).ToArray(),wire.AsSpan(58+5*17).ToArray());
    }

    [Fact]
    public void AvatarUpdateReplacesCachedAppearanceWithoutMovingStationAvatar()
    {
        var world=new EnbNext.Net7.World.Net7WorldState();
        var (a,_)=Net7AvatarRosterCodec.DecodeAvatarData(Net7PresentationWireTests.BuildAvatarData(""),"fixture");
        world.ReplaceAvatarAppearance(42,a);
        world.Apply(Packet.ForOpcode(0x9e,Net7PresentationWireTests.BuildStarbaseAvatarChange(42,2,1,new(4,5,6),0)),0);
        var changed=a with {HairNum=(byte)(a.HairNum+1)};
        var bytes=Net7AvatarCreateCodec.EncodeRecustomizationData(changed);var payload=new byte[bytes.Length+4];bytes.CopyTo(payload,0);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(bytes.Length),42);
        world.Apply(Packet.ForOpcode(0x85,payload),0);
        Assert.Equal(changed.HairNum,world.AvatarDescriptions[42].HairNum);
        Assert.Equal(new Vector3(4,5,6),world.StationAvatars[42].Position);
    }
}
