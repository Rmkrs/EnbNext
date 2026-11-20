using System.Reflection;
using EnbNext.Client;
using EnbNext.Client.Models;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Tests;

public sealed class EnbClientTargetVerbTests
{
    [Fact]
    public async Task NpcMessageSendsActionTwoAndPublishesTheServerDialogue()
    {
        await using var client=new EnbClient(new());
        var session=new Net7Session(new Net7ServerOptions {Host="127.0.0.1"});
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        typeof(EnbClient).GetField("_net7",flags)!.SetValue(client,session);
        typeof(EnbClient).GetProperty("State")!.SetValue(client,ClientLifecycleState.InWorld);
        EnbR11FlightTests.FlightWorld(session.World);
        byte[] create=new byte[23];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(create,23);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(create.AsSpan(4),1);
        session.World.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(4,create),1000);
        session.Gameplay.SetLocalSelectedTarget(23);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime=receiver;
        var frames=new List<(ushort Opcode,byte[] Payload)>();
        channel.FrameSent+=(frame,_)=>frames.Add((frame.Opcode,frame.Payload.ToArray()));
        Assert.False(await client.ExecuteTargetVerbAsync(24,0x00050000));
        Assert.False(await client.ExecuteTargetVerbAsync(23,0x00050001));
        Assert.Empty(frames);
        Assert.True(await client.ExecuteTargetVerbAsync(23,0x00050000));
        var request=Assert.Single(frames);
        Assert.Equal(0x002C,request.Opcode);
        Assert.Equal(Net7GameplayWire.BuildAction(7,Net7PlayerAction.MessageNonAvatar,23,0),request.Payload);
        // Synthetic server reply, using the reported NPC text and one response.
        // The request must not fabricate a tree or issue a station request.
        Assert.Null(session.Gameplay.TalkTree);
        using var body=new MemoryStream();
        using(var writer=new BinaryWriter(body,System.Text.Encoding.UTF8,true))
        {
            writer.Write(System.Text.Encoding.UTF8.GetBytes("Please use caution at the construction site.\0\0"));
            writer.Write((byte)1);writer.Write(0u);
            writer.Write(System.Text.Encoding.UTF8.GetBytes("What are you doing here?\0"));
        }
        var publish=typeof(EnbClient).GetMethod("OnNet7PresentationStateChanged",flags)!;
        session.PresentationStateChanged+=time=>publish.Invoke(client,[time]);
        var dispatch=typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",flags)!;
        await (Task)dispatch.Invoke(session,[EnbNext.Protocol.Net.Packet.ForOpcode(0x54,body.ToArray()),"offline NPC response",CancellationToken.None])!;
        var conversation=client.LatestPresentationSnapshot!.StationConversation!;
        Assert.Equal("Please use caution at the construction site.",conversation.Tree!.NpcSay);
        Assert.Equal("What are you doing here?",Assert.Single(conversation.Tree.Responses).Text);
        // Player ships use the separate local-chat branch, never NPC action 2.
        create[10]=1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(create,24);
        session.World.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(4,create),1000);
        session.Gameplay.SetLocalSelectedTarget(24);
        Assert.False(await client.ExecuteTargetVerbAsync(24,0x00050000));
        Assert.Single(frames);
    }

    [Fact]
    public async Task VerbActionsUseTheAssignedObjectAndSelfTradeOrFollowReportLocally()
    {
        // 00658430: every verb Action uses SClient+112C (0037), never Start's
        // +1138; Scan/Tractor address +112C; Trade/Follow on the own object add
        // local type 0x11 text and queue nothing.
        await using var client=new EnbClient(new());
        var session=new Net7Session(new Net7ServerOptions {Host="127.0.0.1"});
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        typeof(EnbClient).GetField("_net7",flags)!.SetValue(client,session);
        typeof(EnbClient).GetProperty("State")!.SetValue(client,ClientLifecycleState.InWorld);
        EnbR11FlightTests.FlightWorld(session.World);
        const uint assigned=0x40000007;
        typeof(EnbNext.Net7.World.Net7WorldState).GetProperty("ClientAvatarId")!.SetValue(session.World,assigned);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime=receiver;
        var frames=new List<byte[]>();
        channel.FrameSent+=(frame,_)=>{if(frame.Opcode==0x002C)frames.Add(frame.Payload.ToArray());};

        session.Gameplay.SetLocalSelectedTarget(23);
        Assert.True(await client.ExecuteTargetVerbAsync(23,0x00010000));
        Assert.True(await client.ExecuteTargetVerbAsync(23,0x00070000));
        Assert.True(await client.ExecuteTargetVerbAsync(23,0x00080000));
        Assert.Equal(Net7GameplayWire.BuildAction(assigned,Net7PlayerAction.Scan,assigned,0),frames[0]);
        Assert.Equal(Net7GameplayWire.BuildAction(assigned,Net7PlayerAction.Tractor,assigned,0),frames[1]);
        Assert.Equal(Net7GameplayWire.BuildAction(assigned,Net7PlayerAction.DockRequest,23,0),frames[2]);

        session.Gameplay.SetLocalSelectedTarget(assigned);
        Assert.True(await client.ExecuteTargetVerbAsync(assigned,0x00060000));
        Assert.True(await client.ExecuteTargetVerbAsync(assigned,0x000D0000));
        Assert.Equal(3,frames.Count);
        Assert.Equal(["Cannot trade with yourself.","Cannot autofollow self."],
            session.Gameplay.Messages.TakeLast(2).Select(m=>m.Text));
        Assert.All(session.Gameplay.Messages.TakeLast(2),m=>Assert.Equal(0x11,m.Type));
    }

    [Fact]
    public async Task FlightAndPlayerObjectPacketsUseTheAssignedObject()
    {
        // 005806E0/00583790/00583510 (Move/Turn/Tilt), 007241F0 (Fire) and
        // 00581670/00583250 (0x008D..0x008F) pass SClient+112C, not Start.
        await using var session=new Net7Session(new Net7ServerOptions {Host="127.0.0.1"});
        EnbR11FlightTests.FlightWorld(session.World);
        const uint assigned=0x40000007;
        typeof(EnbNext.Net7.World.Net7WorldState).GetProperty("ClientAvatarId")!.SetValue(session.World,assigned);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime=receiver;
        var frames=new List<(ushort Opcode,byte[] Payload)>();
        channel.FrameSent+=(frame,_)=>frames.Add((frame.Opcode,frame.Payload.ToArray()));
        Assert.True(await session.SendMoveAsync(1));
        Assert.True(await session.SendHorizontalSteeringAsync(.5f));
        Assert.True(await session.SendVerticalSteeringAsync(-.5f));
        Assert.True(await session.SendFireAsync(2));
        Assert.True(await session.RequestIncapacitationDetailsAsync());
        // 0065CCD0/0074ABC0: each 0x0033 producer loads +112C immediately before the build.
        Assert.True(await session.SendPlayerCommandAsync(Net7ChatCommandType.LocalArea,"hi"));
        Assert.Contains(frames,f=>f.Opcode==0x0033 && f.Payload.SequenceEqual(Net7GameplayWire.BuildPlayerCommand(assigned,Net7ChatCommandType.LocalArea,"hi")));
        Assert.Contains(frames,f=>f.Opcode==0x0014 && f.Payload.SequenceEqual(Net7GameplayWire.BuildMove(assigned,1)));
        Assert.Contains(frames,f=>f.Opcode==0x0012 && f.Payload.SequenceEqual(Net7GameplayWire.BuildSteering(assigned,.5f)));
        Assert.Contains(frames,f=>f.Opcode==0x0013 && f.Payload.SequenceEqual(Net7GameplayWire.BuildSteering(assigned,-.5f)));
        Assert.Contains(frames,f=>f.Opcode==0x0015 && f.Payload.SequenceEqual(Net7GameplayWire.BuildFire(assigned,2)));
        Assert.Contains(frames,f=>f.Opcode==0x008D && f.Payload.SequenceEqual(Net7GameplayWire.BuildIncapacitationRequest(assigned)));
    }

    [Fact]
    public async Task LandVerbQueuesRequestThenControllerCommitWithoutServerCameraAndKeepsSelectedPlanet()
    {
        await using var client=new EnbClient(new());
        var session=new Net7Session(new Net7ServerOptions {Host="127.0.0.1"});
        typeof(EnbClient).GetField("_net7",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(client,session);
        typeof(EnbClient).GetProperty("State")!.SetValue(client,ClientLifecycleState.InWorld);
        EnbR11FlightTests.FlightWorld(session.World);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime=receiver;
        session.Gameplay.SetLocalSelectedTarget(23);
        var actions=new List<byte[]>();
        channel.FrameSent+=(frame,_)=>
        {
            if(frame.Opcode!=0x002C)return;
            actions.Add(frame.Payload.ToArray());
            // Selection changes after request must not redirect landing.
            session.Gameplay.SetLocalSelectedTarget(99);
            session.PlayerSemantics.ObserveServerTransitionTarget(EnbNext.Net7.Gameplay.Net7TransitionKind.Land, 88);
        };
        Assert.True(await client.ExecuteTargetVerbAsync(23,0x00020000));
        Assert.True(session.World.Objects.FindFirst(7)!.GlobalWarpUpdatesSuppressed);
        Assert.Single(actions);
        var pending = Assert.IsType<Net7LandCameraTransition>(session.Gameplay.LandCamera);
        Assert.Equal(8,session.Gameplay.NativeCameraEvents[^1].Event);
        var controller = new EnbNext.Rendering.EnbRetailCameraController();
        var options = EnbCameraControlTests.Camera() with {LandCameraEnabled=false};
        controller.Reset(0,true);controller.Update(options,0,true);controller.Update(options,1,true);
        controller.Enqueue(8);
        Assert.Equal(8,controller.Update(options,2,true).Action);
        Assert.True(await session.CompleteLandCameraAsync(pending.Revision,pending.TargetId,pending.Phase));
        Assert.Equal(2,actions.Count);
        Assert.Equal(Net7GameplayWire.BuildAction(7,Net7PlayerAction.LandRequest,23,0),actions[0]);
        Assert.Equal(Net7GameplayWire.BuildAction(7,Net7PlayerAction.Landing,23,0),actions[1]);
        Assert.Null(session.PlayerSemantics.TransitionRequest);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>session.CommitLandAsync());
        Assert.Equal(2,actions.Count);
    }

    [Theory]
    [InlineData(Net7TransitionKind.Dock,44u)]
    [InlineData(Net7TransitionKind.Land,23u)]
    public async Task ReplacedLandingDoesNotCommitAnotherOperation(Net7TransitionKind replacement,uint target)
    {
        await using var session=new Net7Session(new Net7ServerOptions {Host="127.0.0.1"});
        EnbR11FlightTests.FlightWorld(session.World);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime=receiver;
        int actions=0;
        channel.FrameSent+=(frame,_)=>
        {
            if(frame.Opcode!=0x002C)return;
            actions++;
            session.PlayerSemantics.BeginTransition(replacement,target);
        };
        Assert.False(await session.RequestLandAsync(23,completeWithoutCamera:true));
        Assert.Equal(1,actions);
        Assert.Equal(new Net7TransitionRequestState(replacement,target),session.PlayerSemantics.TransitionRequest);
    }

    [Theory]
    [InlineData(0x00020000u, Net7TransitionKind.Land)]
    [InlineData(0x00080000u, Net7TransitionKind.Dock)]
    [InlineData(0x000A0000u, Net7TransitionKind.Gate)]
    public async Task EnabledTransitionVerbUsesTypedRequestAndRetainsTargetProvenance(uint verb, Net7TransitionKind kind)
    {
        await using var client = new EnbClient(new());
        var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        typeof(EnbClient).GetField("_net7", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, session);
        typeof(EnbClient).GetProperty("State")!.SetValue(client, ClientLifecycleState.InWorld);
        session.World.SetLocalPlayerId(1);
        session.Gameplay.SetLocalSelectedTarget(23);

        // No transport is connected. Exercise facade dispatch, not a live server:
        // the typed request records provenance before ordinary send suppression.
        Assert.False(await client.ExecuteTargetVerbAsync(23, verb));
        Assert.Equal(new Net7TransitionRequestState(kind,23), session.PlayerSemantics.TransitionRequest);
        if (kind != Net7TransitionKind.Land)
            Assert.Equal(0u, session.PlayerSemantics.RequireCommitTarget(kind));
    }

    [Theory]
    [InlineData(24u, 0x000A0000u)]
    [InlineData(23u, 0x000A0001u)]
    public async Task StaleOrDisabledGateVerbDoesNotStartTransition(uint target, uint verb)
    {
        await using var client = new EnbClient(new());
        var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        typeof(EnbClient).GetField("_net7", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, session);
        typeof(EnbClient).GetProperty("State")!.SetValue(client, ClientLifecycleState.InWorld);
        session.World.SetLocalPlayerId(1);
        session.Gameplay.SetLocalSelectedTarget(23);
        Assert.False(await client.ExecuteTargetVerbAsync(target, verb));
        Assert.Null(session.PlayerSemantics.TransitionRequest);
    }
}
