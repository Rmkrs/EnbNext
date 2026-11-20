using System.Buffers.Binary;
using System.Text;
using System.Reflection;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;
namespace EnbNext.Net7.Tests;

public sealed class Net7AutomaticActionsWireTests
{
    [Fact]
    public async Task IngressUsesExactManualRequestsAndOffLeavesDialogsUntouched()
    {
        await using var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        EnbR11FlightTests.FlightWorld(session.World);
        const uint avatar=0x40000007,partner=999;
        session.World.MaterializeCreate(new Net7CreateUpdate(avatar,1,0,0,0,0,0));
        session.World.Apply(Packet.ForOpcode(0x0037,BitConverter.GetBytes(avatar)),1000);
        session.World.Apply(Packet.ForOpcode(0x0047,BitConverter.GetBytes(avatar)),1000);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);using var lifetime=receiver;
        var frames=new List<(ushort Opcode,byte[] Payload)>();
        channel.FrameSent+=(frame,_)=>frames.Add((frame.Opcode,frame.Payload.ToArray()));
        var dispatch=typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        async Task Receive(ushort opcode,byte[] payload)=>await (Task)dispatch.Invoke(session,[Packet.ForOpcode(opcode,payload),"decoded-inner",CancellationToken.None])!;
        var text=Encoding.Latin1.GetBytes("Peer invites you");var invite=new byte[text.Length+4];
        BinaryPrimitives.WriteUInt16LittleEndian(invite,(ushort)(text.Length+1));invite[2]=1;text.CopyTo(invite,3);
        byte[] Trade(byte type){var p=new byte[5];BinaryPrimitives.WriteUInt32LittleEndian(p,partner);p[4]=type;return p;}
        await Receive(0x001E,invite);Assert.NotNull(session.Gameplay.Social.GroupInvite);Assert.Empty(frames);
        // The unanswered invitation is answered first: a second one would otherwise wait behind it (00588A70, GROUP-D13).
        session.Gameplay.Social.ClearGroupInvite();
        session.ConfigureAutomaticActions(true,true,true);
        await Receive(0x001E,invite);Assert.Null(session.Gameplay.Social.GroupInvite);
        Assert.Equal(Net7GameplayWire.BuildAction(avatar,Net7PlayerAction.GroupAccept,avatar,0),Assert.Single(frames).Payload);
        frames.Clear();await Receive(0x001F,Trade(0));Assert.Empty(frames);
        await Receive(0x001F,Trade(5));
        Assert.Equal(Net7GameplayWire.BuildAction(avatar,Net7PlayerAction.TradeConfirm,partner,0),Assert.Single(frames).Payload);
        await Receive(0x001F,Trade(5));await Receive(0x001F,Trade(3));Assert.Single(frames);
        await Receive(0x001F,Trade(2));Assert.Null(session.Gameplay.TradeDomain.ActivePartnerId);
        session.ConfigureAutomaticActions(false,false,false);frames.Clear();
        await Receive(0x001F,Trade(0));await Receive(0x001F,Trade(5));Assert.Empty(frames);
        Assert.True(session.Gameplay.TradeDomain.Confirmed);
        var schema=new Net7WorldState().LocalPlayerAuxData;
        var operations=new List<Net7AuxDataOperation>();
        void Set(string path,object value){var p=schema.Properties.Single(p=>p.Path=="LocalPlayerAuxData.GroupInfo."+path);p.Value=value;p.State=1;operations.Add(new(p.Id,Net7AuxDataOperationKind.ValueUpdate));}
        async Task GroupUpdate()
        {
            var delta=new Net7AuxDataCodec().Encode(schema,operations);operations.Clear();var payload=new byte[6+delta.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4),(ushort)delta.Length);delta.CopyTo(payload,6);
            await Receive(0x001B,payload);
        }
        Set("Members.0.Name",new Net7AuxDataString(Encoding.Latin1.GetBytes("Peer")));Set("Members.0.GameID",(int)partner);
        Set("IsGroupLeader",(byte)0);Set("FormationName",new Net7AuxDataString(Encoding.Latin1.GetBytes("Pipe")));
        Set("Formation",new byte[4]);
        session.ConfigureAutomaticActions(false,false,true);
        await GroupUpdate();
        Assert.Equal(Net7GameplayWire.BuildCtaRequest(avatar,0,7),Assert.Single(frames).Payload);
        Set("FormationName",new Net7AuxDataString(Encoding.Latin1.GetBytes("Pipe")));await GroupUpdate();Assert.Single(frames);
        Set("FormationName",new Net7AuxDataString(Encoding.Latin1.GetBytes("Block")));await GroupUpdate();Assert.Equal(2,frames.Count);
        session.ConfigureAutomaticActions(false,false,false);
        Set("FormationName",new Net7AuxDataString(Encoding.Latin1.GetBytes("Slot")));await GroupUpdate();Assert.Equal(2,frames.Count);
        await Receive(0x001E,invite);Assert.NotNull(session.Gameplay.Social.GroupInvite);
    }
}
