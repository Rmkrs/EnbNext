using System.Buffers.Binary;
using System.Reflection;
using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;
namespace EnbNext.Net7.Tests;

public sealed class Net7LandCameraCompletionTests
{
    [Fact]
    public async Task EnabledLandingPublishesWithoutMovementAndCommitsOriginalTargetOnce()
    {
        await using var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        EnbR11FlightTests.FlightWorld(session.World);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);
        using var lifetime=receiver;
        var actions=new List<byte[]>();int published=0;
        channel.FrameSent+=(frame,_)=>{if(frame.Opcode==0x002C)actions.Add(frame.Payload.ToArray());};
        session.PresentationStateChanged+=_=>published++;
        Assert.True(await session.RequestLandAsync(23,startCamera:true));
        var camera=Assert.IsType<Net7LandCameraTransition>(session.Gameplay.LandCamera);
        Assert.True(published>0);Assert.Single(actions);
        session.Gameplay.SetLocalSelectedTarget(99);
        Assert.False(await session.CompleteLandCameraAsync(camera.Revision,99,camera.Phase));
        Assert.False(await session.CompleteLandCameraAsync(camera.Revision,23,Net7LandCameraPhase.SurfaceArrival));
        Assert.True(await session.CompleteLandCameraAsync(camera.Revision,23,camera.Phase));
        Assert.Equal(Net7GameplayWire.BuildAction(7,Net7PlayerAction.Landing,23,0),actions[1]);
        Assert.False(await session.CompleteLandCameraAsync(camera.Revision,23,camera.Phase));Assert.Equal(2,actions.Count);
    }
    [Fact]
    public async Task ReplacedLandingCannotCommitAndSurfaceArrivalHasNoWireCompletion()
    {
        await using var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        EnbR11FlightTests.FlightWorld(session.World);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);
        using var lifetime=receiver;
        int actions=0,published=0;
        channel.FrameSent+=(frame,_)=>{if(frame.Opcode==0x002C)actions++;};
        session.PresentationStateChanged+=_=>published++;
        await session.RequestLandAsync(23,startCamera:true);var old=session.Gameplay.LandCamera!;
        await session.RequestDockAsync(22);
        Assert.Null(session.Gameplay.LandCamera);
        Assert.False(await session.CompleteLandCameraAsync(old.Revision,old.TargetId,old.Phase));
        byte[] bytes=new byte[8];BinaryPrimitives.WriteUInt32BigEndian(bytes,1);BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4),23);
        async Task Dispatch()=>await (Task)typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",BindingFlags.NonPublic|BindingFlags.Instance)!
            .Invoke(session,[Packet.ForOpcode(0x0092,bytes),"offline",CancellationToken.None])!;
        int before=published;await Dispatch();
        var arrival=Assert.IsType<Net7LandCameraTransition>(session.Gameplay.LandCamera);
        Assert.True(published>before);Assert.Equal(Net7LandCameraPhase.SurfaceArrival,arrival.Phase);
        await Dispatch();Assert.Same(arrival,session.Gameplay.LandCamera);
        Assert.True(await session.CompleteLandCameraAsync(arrival.Revision,arrival.TargetId,arrival.Phase));
        Assert.Equal(2,actions);
        await Dispatch();session.Gameplay.ResetForServerHandoff();Assert.Null(session.Gameplay.LandCamera);
    }
}
