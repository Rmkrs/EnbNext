using System.Buffers.Binary;
using System.Reflection;
using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7G15CameraIntegrationTests
{
    [Theory]
    [InlineData(0,12)] [InlineData(1,13)] [InlineData(2,0)] [InlineData(3,15)] [InlineData(4,6)]
    [InlineData(5,10)] [InlineData(6,23)] [InlineData(7,16)] [InlineData(8,25)] [InlineData(9,-1)]
    public async Task WireSelectorsPublishNativeEventsWithoutReinterpretingWireEncoding(uint selector,int expected)
    {
        await using var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        EnbR11FlightTests.FlightWorld(session.World);
        if(selector==7)session.Gameplay.AddCameraEvent(3,99);
        var (_,_,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);using var lifetime=receiver;
        byte[] bytes=new byte[8];BinaryPrimitives.WriteUInt32BigEndian(bytes,selector);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4),99);
        await (Task)typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",BindingFlags.NonPublic|BindingFlags.Instance)!
            .Invoke(session,[Packet.ForOpcode(0x0092,bytes),"offline",CancellationToken.None])!;
        if(expected<0)Assert.Empty(session.Gameplay.NativeCameraEvents);
        else Assert.Equal(expected,session.Gameplay.NativeCameraEvents[^1].Event);
        if(selector==7)Assert.Null(session.Gameplay.LootCamera);
        Assert.Equal(selector is 4 or 5, session.World.Objects.FindFirst(7)!.GlobalWarpUpdatesSuppressed);
    }

    [Fact]
    public async Task PlanetWarpButtonQueuesTakeoffAndCompletionIsOwnedAndSentOnce()
    {
        await using var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        EnbR11FlightTests.FlightWorld(session.World);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);using var lifetime=receiver;
        typeof(Net7GameplayState).GetProperty(nameof(Net7GameplayState.PresentationMode))!.SetValue(session.Gameplay,1);
        var actions=new List<byte[]>();int warpPackets=0;
        channel.FrameSent+=(frame,_)=>{if(frame.Opcode==0x002c)actions.Add(frame.Payload.ToArray());if(frame.Opcode==0x009b)warpPackets++;};
        Assert.True(await session.RequestWarpAsync(7,[]));
        Assert.Equal(17,session.Gameplay.NativeCameraEvents[^1].Event);
        var revision=session.Gameplay.TakeoffRevision!.Value;
        Assert.Empty(actions);Assert.Equal(0,warpPackets);
        Assert.False(await session.CompleteTakeoffCameraAsync(revision+1));
        Assert.True(await session.CompleteTakeoffCameraAsync(revision));
        Assert.Equal(Net7GameplayWire.BuildAction(7,Net7PlayerAction.CameraControllerSelfAction,7,0),Assert.Single(actions));
        Assert.False(await session.CompleteTakeoffCameraAsync(revision));
        Assert.True(await session.RequestWarpAsync(7,[]));
        revision=session.Gameplay.TakeoffRevision!.Value;
        session.Gameplay.ResetForServerHandoff();
        Assert.False(await session.CompleteTakeoffCameraAsync(revision));Assert.Single(actions);
    }
}
