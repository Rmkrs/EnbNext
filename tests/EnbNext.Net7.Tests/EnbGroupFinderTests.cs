using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using EnbNext.Client;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Social;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class EnbGroupFinderTests
{
    [Fact]
    public async Task SessionFinderUsesLoadedIdentityAtResponseTimeAndPreservesEmptyNames()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        EnbR11FlightTests.FlightWorld(session.World);
        session.World.MaterializeCreate(new Net7CreateUpdate(8,1,0,1,0,0,0));
        var ship = session.World.Objects.FindFirst(8)!;
        var owner = ship.AuxData!.Properties.Single(p => p.Path == "ShipAuxData.Owner");
        var name = ship.AuxData.Properties.Single(p => p.Path == "ShipAuxData.Name");
        owner.State = name.State = 0;
        var social = session.Gameplay.Social;
        social.SetGroupFinderWindow(true);
        byte[] payload = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(payload,2);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4),8);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20),999);
        var dispatch = typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task Receive() => await (Task)dispatch.Invoke(session,[Packet.ForOpcode(0x53,payload),"offline-finder",CancellationToken.None])!;
        await Receive();
        Assert.Equal(new[] { "Unknown", "Anonymous" }, social.GroupFinderRows.Select(r => r.Name));
        name.Value = new Net7AuxDataString(Encoding.Latin1.GetBytes("Ship name")); name.State = 1;
        await Receive(); Assert.Equal("Ship name",social.GroupFinderRows[0].Name);
        owner.Value = new Net7AuxDataString(Encoding.Latin1.GetBytes("Pilot name")); owner.State = 1;
        await Receive(); Assert.Equal("Pilot name",social.GroupFinderRows[0].Name);
        var retained = social.GroupFinderRows;
        owner.Value = new Net7AuxDataString([]);
        Assert.Equal("Pilot name",social.GroupFinderRows[0].Name);
        await Receive(); Assert.Equal("",social.GroupFinderRows[0].Name);
        Assert.Equal("Pilot name",retained[0].Name);
        social.SetGroupFinderWindow(false);
        await Receive(); Assert.Empty(social.GroupFinderRows);
    }

    [Fact]
    public void ArrivalOrderAndAllNativeSortsSurviveSnapshotAndReset()
    {
        var social = new Net7SocialState(id => id switch { 2 or 3 => "Same", 4 => "Alpha", _ => null }, () => 1);
        Net7FindMemberRecord[] input = [new(1,0,0,0),new(2,10,0,0),new(3,1,1,0),new(4,uint.MaxValue,2,0),new(5,20,9,9)];
        social.ApplyFindMember(input);
        Assert.Empty(social.GroupFinderRows);
        Assert.Equal(4,social.GroupFinderCandidates.Count);
        social.SetGroupFinderWindow(true); social.ApplyFindMember(input);
        Assert.Equal(new uint[]{2,3,4,5},social.GroupFinderRows.Select(r=>r.ObjectId));
        Assert.Equal(new uint[]{4,5,2,3},EnbGroupFinder.Sort(social.GroupFinderRows,0).Select(r=>r.ObjectId));
        Assert.Equal(new uint[]{3,4,2,5},EnbGroupFinder.Sort(social.GroupFinderRows,1).Select(r=>r.ObjectId));
        Assert.Equal(new uint[]{4,3,2,5},EnbGroupFinder.Sort(social.GroupFinderRows,2).Select(r=>r.ObjectId));
        Assert.Equal("Anonymous",social.GroupFinderRows[3].Name);
        Assert.Equal("Ter/Enf",social.GroupFinderRows[3].CurrentClassLabel);
        var retained = social.GroupFinderRows; long revision = social.GroupFinderRevision;
        social.ApplyFindMember(input); Assert.True(social.GroupFinderRevision > revision);
        social.SetGroupFinderWindow(false); social.ApplyFindMember(input);
        Assert.Empty(social.GroupFinderRows); Assert.Equal(4,retained.Count);
        social.SetGroupFinderWindow(true); Assert.Empty(social.GroupFinderRows);
        social.ResetForCharacterSelection(); Assert.False(social.GroupFinderWindowActive); Assert.Empty(social.GroupFinderRows);
    }

    [Fact]
    public async Task ActualFacadeBindsRoleOptionsSearchTargetAndCloseWithoutChangingAuthority()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        await using var client = new EnbClient(new());
        var session = new Net7Session(new Net7ServerOptions {Host="127.0.0.1"});
        typeof(EnbClient).GetField("_net7",flags)!.SetValue(client,session);
        typeof(EnbClient).GetProperty("State")!.SetValue(client,ClientLifecycleState.InWorld);
        EnbR11FlightTests.FlightWorld(session.World);
        session.World.MaterializeCreate(new Net7CreateUpdate(8,1,0,0,0,0,0));
        var (_,channel,receiver) = await Net7R11FlightWireTests.AttachLoopback(session); using var lifetime=receiver;
        var frames = new List<(ushort Opcode,byte[] Payload)>();
        channel.FrameSent += (f,_)=>frames.Add((f.Opcode,f.Payload.ToArray()));
        void Publish(uint time=1000) => typeof(EnbClient).GetMethod("OnNet7PresentationStateChanged",flags)!.Invoke(client,[time]);
        session.PresentationStateChanged += Publish; Publish();
        var authoritativeSettings = session.PlayerSemantics.GetGroupFinderSettings();
        ulong owner=client.LatestPresentationSnapshot!.Objects.Single(o=>o.IsLocalShip).RenderIdentity;
        Task<bool> Send(EnbGroupFinderAction action,uint option=0,bool enabled=false,uint target=0,long revision=0)
            => client.GroupFinderAsync(new(7,owner,action,option,enabled,target,revision));
        Assert.False(await client.GroupFinderAsync(new(9,owner,EnbGroupFinderAction.Search)));
        Assert.False(await client.GroupFinderAsync(new(7,owner+1,EnbGroupFinderAction.Search)));
        Assert.Empty(frames);
        Assert.True(await Send(EnbGroupFinderAction.Search));
        Assert.True(session.Gameplay.Social.GroupFinderWindowActive);
        Assert.Equal(16,Net7GameplayWire.DecodeAction(frames.Single().Payload).Action);
        Assert.True(await Send(EnbGroupFinderAction.Option,0,true));
        Assert.True(await Send(EnbGroupFinderAction.Option,1,true));
        Assert.True(await Send(EnbGroupFinderAction.Option,5,true));
        Assert.False(await Send(EnbGroupFinderAction.Option,3,true));
        Assert.False(await Send(EnbGroupFinderAction.Option,4,true));
        Assert.False(await Send(EnbGroupFinderAction.Option,2,true));
        void Set(string path,object value)
        {var p=session.World.LocalPlayerAuxData.Properties.Single(p=>p.Path=="LocalPlayerAuxData.GroupInfo."+path);p.Value=value;p.State=2;p.UpdateRevision++;}
        Set("Members.0.Name",new Net7AuxDataString(Encoding.Latin1.GetBytes("Peer")));
        Set("IsGroupLeader",(byte)0); Publish();
        Assert.False(await Send(EnbGroupFinderAction.Option,0,true));
        Assert.False(await Send(EnbGroupFinderAction.Option,5,true));
        Assert.True(await Send(EnbGroupFinderAction.Leave));
        Assert.False(await Send(EnbGroupFinderAction.Disband));
        Set("IsGroupLeader",(byte)1); Publish();
        foreach(uint option in new uint[]{1,3,4,5})Assert.True(await Send(EnbGroupFinderAction.Option,option,true));
        Assert.True(await Send(EnbGroupFinderAction.Disband));
        Assert.False(await Send(EnbGroupFinderAction.Leave));
        Assert.Equal("Peer",session.PlayerSemantics.GetGroupState().Members[0].Name);
        Assert.Equal(authoritativeSettings,session.PlayerSemantics.GetGroupFinderSettings());
        Assert.Equal(new uint[]{0,1,5,1,3,4,5},frames.Where(f=>f.Opcode==0x2E).Select(f=>BinaryPrimitives.ReadUInt32LittleEndian(f.Payload.AsSpan(4))));
        Assert.All(frames.Where(f=>f.Opcode==0x2E),f=>Assert.Equal(Net7GameplayWire.BuildGroupFinderOption(7,BinaryPrimitives.ReadUInt32LittleEndian(f.Payload.AsSpan(4)),true),f.Payload));
        Assert.Equal(new[]{16,14,13},frames.Where(f=>f.Opcode==0x2C).Select(f=>Net7GameplayWire.DecodeAction(f.Payload).Action));
        byte[] payload=new byte[36];BinaryPrimitives.WriteUInt32LittleEndian(payload,2);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4),8);BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8),42);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20),999);BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(24),7);
        async Task Receive() => await (Task)typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",flags)!.Invoke(session,[Packet.ForOpcode(0x53,payload),"offline-finder",CancellationToken.None])!;
        await Receive();var snapshot=client.LatestPresentationSnapshot.Hud.Group!;
        Assert.Equal(2,snapshot.FinderRows.Length);
        int before=frames.Count;
        Assert.False(await Send(EnbGroupFinderAction.Target,target:999,revision:snapshot.FinderRevision));
        Assert.False(await Send(EnbGroupFinderAction.Target,target:8,revision:snapshot.FinderRevision-1));
        Assert.Equal(before,frames.Count);
        Assert.True(await Send(EnbGroupFinderAction.Target,target:8,revision:snapshot.FinderRevision));
        Assert.Equal(8u,session.Gameplay.LocalSelectedTargetId);
        Assert.True(await Send(EnbGroupFinderAction.CloseList));
        Assert.Empty(client.LatestPresentationSnapshot.Hud.Group!.FinderRows);
        await Receive();Assert.Empty(client.LatestPresentationSnapshot.Hud.Group!.FinderRows);
        Assert.Equal(2,snapshot.FinderRows.Length);
        Assert.False(await Send(EnbGroupFinderAction.Target,target:8,revision:snapshot.FinderRevision));
        Assert.True(await Send(EnbGroupFinderAction.Search));Assert.Empty(client.LatestPresentationSnapshot.Hud.Group!.FinderRows);
    }
}
