using System.Buffers.Binary;
using System.Text;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Social;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class EnbGroupPresentationTests
{
    [Fact]
    public async Task GroupProducersUseAssignedIdentityAndPreserveAuthority()
    {
        await using var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        EnbR11FlightTests.FlightWorld(session.World);
        const uint avatar=0x40000007;
        session.World.MaterializeCreate(new Net7CreateUpdate(avatar,1,0,0,0,0,0));
        session.World.Apply(Packet.ForOpcode(0x0037,BitConverter.GetBytes(avatar)),1000);
        session.World.MaterializeCreate(new Net7CreateUpdate(999,1,0,0,0,0,0)); // the member is in view: 005949AA kicks by ID
        session.World.Apply(Packet.ForOpcode(0x0047,BitConverter.GetBytes(avatar)),1000);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);
        using var lifetime=receiver;
        var frames=new List<(ushort Opcode,byte[] Payload)>();
        channel.FrameSent+=(frame,_)=>frames.Add((frame.Opcode,frame.Payload.ToArray()));
        void Set(string path,object value)
        {var p=session.World.LocalPlayerAuxData.Properties.Single(p=>p.Path=="LocalPlayerAuxData.GroupInfo."+path);p.Value=value;p.State=1;}
        Set("Members.0.Name",new Net7AuxDataString(Encoding.Latin1.GetBytes("Peer")));Set("Members.0.GameID",999);
        Set("IsGroupLeader",(byte)0);
        Assert.True(await session.AcceptGroupInviteAsync());Assert.True(await session.RefuseGroupInviteAsync());
        Assert.True(await session.LeaveGroupAsync());
        Assert.False(await session.SendGroupCtaPanelRequestAsync(Net7GroupCtaPanelRequestType.Request4));
        Assert.True(await session.SendGroupCtaPanelRequestAsync(Net7GroupCtaPanelRequestType.Request7));
        Assert.True(await session.SendGroupCtaMember12Async(0));Assert.True(await session.SendGroupCtaTopLevel12Async());
        Set("IsGroupLeader",(byte)1);
        Assert.True(await session.LeaveGroupAsync());Assert.True(await session.KickGroupMemberByIndexAsync(0));
        Assert.True(await session.SendGroupCtaPanelRequestAsync(Net7GroupCtaPanelRequestType.Request4));
        Assert.All(frames,f=>Assert.Equal(avatar,BinaryPrimitives.ReadUInt32LittleEndian(f.Payload)));
        var actions=frames.Where(f=>f.Opcode==0x002C).Select(f=>Net7GameplayWire.DecodeAction(f.Payload)).ToArray();
        Assert.Equal(new[]{11,12,14,13,15},actions.Select(a=>a.Action));
        Assert.All(actions.Take(4),a=>Assert.Equal(avatar,a.Target));Assert.Equal(999u,actions.Last().Target);
        Assert.Equal("Peer",session.PlayerSemantics.GetGroupState().Members[0].Name);
        Assert.Equal(7u,session.World.LocalPlayerId);
        Assert.Equal(new[]{7u,12u,12u,4u},frames.Where(f=>f.Opcode==0x00BC).Select(f=>BinaryPrimitives.ReadUInt32LittleEndian(f.Payload.AsSpan(8))));
        var owner=session.World.Objects.FindFirst(avatar)!.AuxData!.Properties.Single(p=>p.Path=="ShipAuxData.Owner");
        owner.Value=new Net7AuxDataString(Encoding.Latin1.GetBytes("Initiate Kestrabel"));owner.State=1;
        Assert.True(await session.SendGroupSymbolAsync("G_YES",false));Assert.True(await session.SendGroupSymbolAsync("G_TARGET",true));
        var signals=frames.Where(f=>f.Opcode==0x0033).ToArray();Assert.Equal(2,signals.Length);
        Assert.Equal(Net7GameplayWire.BuildPlayerCommand(avatar,Net7ChatCommandType.Group,"<S>0Kestrabel`G_YES"),signals[0].Payload);
        Assert.Equal(Net7GameplayWire.BuildPlayerCommand(avatar,Net7ChatCommandType.Group,"<S>1Kestrabel`G_TARGET"),signals[1].Payload);
    }

    [Fact]
    public async Task InvitationPublishesImmediatelyAndGroupSnapshotIsDetached()
    {
        await using var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        EnbR11FlightTests.FlightWorld(session.World);
        var publisher=new EnbPresentationSnapshotPublisher();int changes=0;
        session.PresentationStateChanged+=time=>{changes++;publisher.Publish(session.World,session.Gameplay.Effects,null,time,gameplay:session.Gameplay);};
        var dispatch=typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        var text=Encoding.Latin1.GetBytes("Peer is requesting you to join their group");var payload=new byte[text.Length+4];
        BinaryPrimitives.WriteUInt16LittleEndian(payload,(ushort)(text.Length+1));payload[2]=1;text.CopyTo(payload,3);
        await (Task)dispatch.Invoke(session,[Packet.ForOpcode(0x001E,payload),"decoded-inner",CancellationToken.None])!;
        var group=publisher.Latest!.Hud.Group!;
        Assert.Equal(Encoding.Latin1.GetString(text),group.Invitation);Assert.Equal(1,changes);Assert.Equal(5,group.Members.Length);
        session.Gameplay.Social.ClearGroupInvite();Assert.NotNull(group.Invitation);
        await (Task)dispatch.Invoke(session,[Packet.ForOpcode(0x001E,payload),"decoded-inner",CancellationToken.None])!;
        Assert.Equal(group.Invitation,publisher.Latest!.Hud.Group!.Invitation);
        Assert.True(publisher.Latest.Hud.Group.InvitationRevision>group.InvitationRevision);
    }

    [Theory]
    [InlineData(1000u)]
    [InlineData(60000u)]
    public async Task FormationResponseDoesNotStopFollowingLeaderMovement(uint receivedAt)
    {
        await using var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"},clock:new FormationClock(receivedAt));
        EnbR11FlightTests.FlightWorld(session.World);
        session.World.MaterializeCreate(new Net7CreateUpdate(8,1,0,0,0,0,0));
        var dispatch=typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        async Task Receive(ushort opcode,byte[] payload) => await (Task)dispatch.Invoke(session,[Packet.ForOpcode(opcode,payload),"decoded-inner",CancellationToken.None])!;
        await Receive(0x00BD,Convert.FromHexString("070000000700000001"));
        Assert.Equal(7u,session.Gameplay.Social.LastUnhandledCtaResponse!.RequestType);
        byte[] leader=new byte[46];
        BinaryPrimitives.WriteUInt16LittleEndian(leader,1);
        BinaryPrimitives.WriteUInt32LittleEndian(leader.AsSpan(2),8);
        BinaryPrimitives.WriteSingleLittleEndian(leader.AsSpan(34),1);
        BinaryPrimitives.WriteUInt32LittleEndian(leader.AsSpan(38),1);
        BinaryPrimitives.WriteSingleLittleEndian(leader.AsSpan(42),4);
        await Receive(0x003E,leader);
        byte[] formation=new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(formation,7);
        BinaryPrimitives.WriteUInt32LittleEndian(formation.AsSpan(4),8);
        BinaryPrimitives.WriteSingleLittleEndian(formation.AsSpan(8),50);
        await Receive(0x0041,formation);
        var publisher=new EnbPresentationSnapshotPublisher();
        var positions=new List<System.Numerics.Vector3>();
        foreach(uint time in new uint[]{receivedAt+9000,receivedAt+10000,receivedAt+11000})
        {
            publisher.Publish(session.World,session.Gameplay.Effects,null,time,gameplay:session.Gameplay);
            var local=publisher.Latest!.Objects.Single(o=>o.IsLocalShip).Spatial!.Transform;
            var anchor=publisher.Latest.Objects.Single(o=>o.GameId==8).Spatial!.Transform;
            Assert.Equal(anchor.Position+new System.Numerics.Vector3(50,0,0),local.Position);
            Assert.Equal(anchor.Velocity,local.Velocity);
            positions.Add(local.Position);
        }
        Assert.NotEqual(positions[0],positions[1]);
        Assert.NotEqual(positions[1],positions[2]);
    }
    private sealed class FormationClock(uint now) : EnbNext.Net7.Timing.INet7RuntimeClock
    {
        public uint GetMilliseconds() => now;
        public Task DelayAsync(TimeSpan delay,CancellationToken cancellationToken=default)
            => Task.Delay(delay,cancellationToken);
    }
}
