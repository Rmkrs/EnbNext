using System.Reflection;
using System.Buffers.Binary;
using EnbNext.Client;
using EnbNext.Client.Models;
using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;
public sealed class EnbAutopilotGateIntegrationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GateUsesNormalCameraCompletionAfterCommandCancellation(bool cancel)
    {
        await using var client=new EnbClient(new());
        var session=new Net7Session(new Net7ServerOptions {Host="127.0.0.1"});
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(EnbClient).GetField("_net7",flags)!.SetValue(client,session);
        typeof(EnbClient).GetProperty("State")!.SetValue(client,ClientLifecycleState.InWorld);
        EnbR11FlightTests.FlightWorld(session.World);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime=receiver;
        var actions=new List<byte[]>();channel.FrameSent+=(f,_)=>{if(f.Opcode==0x002C)actions.Add(f.Payload.ToArray());};
        var publish=typeof(EnbClient).GetMethod("OnNet7PresentationStateChanged",flags)!;
        session.PresentationStateChanged+=t=>publish.Invoke(client,[t]);
        publish.Invoke(client,[1000u]);
        session.Gameplay.SetLocalSelectedTarget(23);
        using var stop=new CancellationTokenSource();
        Assert.True(await client.ExecuteTargetVerbAsync(23,0x000A0000,stop.Token));
        if(cancel)stop.Cancel();
        byte[] body=new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(body,4);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4),22);
        await (Task)typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",flags)!.Invoke(session,
            [Packet.ForOpcode(0x0092,body),"offline gate",CancellationToken.None])!;
        publish.Invoke(client,[1001u]);
        var frame=client.SampleLivePresentationFrame()!;
        try
        {
            var camera=frame.Camera.GateTransition!;
            Assert.NotNull(camera);
            var controller=new EnbRetailCameraController();controller.Reset(0,true);
            controller.Update(frame.Camera,0,true);controller.Update(frame.Camera,1,true);
            controller.Enqueue(6);
            Assert.Equal(0x13,controller.Update(frame.Camera,2,true).Action);
            ulong owner=frame.Objects.Single(o=>o.IsLocalShip).RenderIdentity;
            Assert.True(await client.CompleteGateCameraAsync(owner,camera.Sequence,camera.TargetId));
            Assert.False(await client.CompleteGateCameraAsync(owner,camera.Sequence,camera.TargetId));
            Assert.Equal(2,actions.Count);
            Assert.Equal(Net7GameplayWire.BuildAction(7,Net7PlayerAction.Gate,22,0),actions[1]);
        }
        finally {frame.Release();}
    }
}
