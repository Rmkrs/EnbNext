using System.Buffers.Binary;
using System.Reflection;
using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7DockCompletionTests
{
    [Fact]
    public async Task EnabledCameraPublishesImmediatelyAndCommitsOnlyItsOwnedCallback()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        EnbR11FlightTests.FlightWorld(session.World);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        var actions = new List<byte[]>();
        channel.FrameSent += (frame, _) => { if(frame.Opcode==0x002C)actions.Add(frame.Payload.ToArray()); };
        int published=0;
        session.PresentationStateChanged += _=>published++;
        Assert.True(await session.RequestDockAsync(11,completeWithoutCamera:false));
        await Dispatch(session,5,22);
        var camera=Assert.IsType<Net7DockCameraTransition>(session.Gameplay.DockCamera);
        Assert.True(published>0);
        Assert.Single(actions);
        Assert.False(await session.CompleteDockCameraAsync(camera.Revision+1,22));
        Assert.False(await session.CompleteDockCameraAsync(camera.Revision,11));
        await Dispatch(session,5,22);
        Assert.Same(camera,session.Gameplay.DockCamera);
        Assert.True(await session.CompleteDockCameraAsync(camera.Revision,22));
        Assert.Equal(Net7GameplayWire.BuildAction(7,Net7PlayerAction.Dock,22,0),actions[1]);
        Assert.False(await session.CompleteDockCameraAsync(camera.Revision,22));
        Assert.Null(session.Gameplay.DockCamera);
        Assert.Equal(2,actions.Count);
    }

    [Fact]
    public async Task DeparturePacketReplacesCameraWithoutCommittingIncomingRequest()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        EnbR11FlightTests.FlightWorld(session.World);
        var (_, _, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        await session.RequestDockAsync(11,completeWithoutCamera:false);
        await Dispatch(session,5,22);
        var old=session.Gameplay.DockCamera!;
        await session.RequestLandAsync(33);
        Assert.Null(session.Gameplay.DockCamera);
        Assert.False(await session.CompleteDockCameraAsync(old.Revision,22));
        await session.RequestDockAsync(11,completeWithoutCamera:false);
        await Dispatch(session,5,22);
        await Dispatch(session,0,22);
        var departure=Assert.IsType<Net7DockCameraTransition>(session.Gameplay.DockCamera);
        Assert.True(departure.IsDeparture);
        Assert.False(await session.CompleteDockCameraAsync(departure.Revision,22));
        Assert.True(await session.CompleteDockCameraAsync(departure.Revision,22,departure:true));
        Assert.NotNull(session.PlayerSemantics.TransitionRequest);
    }

    [Theory]
    [InlineData(5u)]
    public async Task ClearanceCommitsIndependentlyOfLocalRequests(uint message)
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        EnbR11FlightTests.FlightWorld(session.World);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        var actions = new List<byte[]>();
        channel.FrameSent += (frame, _) => { if (frame.Opcode == 0x002C) actions.Add(frame.Payload.ToArray()); };

        await Dispatch(session, message, 0x186A8); // Unsolicited clearance.
        Assert.Equal(message==5?1UL:0UL,session.Gameplay.DockingSoundRevision);
        session.Gameplay.ResetForServerHandoff();
        Assert.Equal(message==5?1UL:0UL,session.Gameplay.DockingSoundRevision);
        Assert.Equal(Net7GameplayWire.BuildAction(7, Net7PlayerAction.Dock, 0x186A8, 0), Assert.Single(actions));
        actions.Clear();
        Assert.True(await session.RequestDockAsync(11));
        Assert.Equal(Net7GameplayWire.BuildAction(7, Net7PlayerAction.DockRequest, 11, 0), Assert.Single(actions));
        await Dispatch(session, 4, 22); // Gate response cannot complete docking.
        Assert.Single(actions);
        await Dispatch(session, message, 0x186A8);
        Assert.Equal(2, actions.Count);
        Assert.Equal(Net7GameplayWire.BuildAction(7, Net7PlayerAction.Dock, 0x186A8, 0), actions[1]);
        Assert.Null(session.PlayerSemantics.TransitionRequest);
        await Dispatch(session, 0, 0x186A8);
        await Dispatch(session, 5, 0x186A8);
        Assert.Equal(3, actions.Count);
        Assert.Equal(actions[1], actions[2]);
    }

    [Theory]
    [InlineData(5u, true, 22u)]
    [InlineData(5u, false, uint.MaxValue)]
    public async Task IncapacitationAndEmptyTargetDoNotCommit(uint message, bool incapacitated, uint target)
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        EnbR11FlightTests.FlightWorld(session.World);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        int actions = 0;
        channel.FrameSent += (frame, _) => { if (frame.Opcode == 0x002C) actions++; };
        var incap = session.World.Objects.FindFirst(7)!.AuxData!.Resolve(28);
        incap.State = 3;
        incap.Value = (byte)(incapacitated ? 1 : 0);
        await session.RequestDockAsync(11);
        await Dispatch(session, message, target);
        Assert.Equal(1, actions);
        Assert.Equal(target, session.PlayerSemantics.RequireCommitTarget(Net7TransitionKind.Dock));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DockClearanceStillCommitsAfterDifferentLocalRequest(bool gate)
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        EnbR11FlightTests.FlightWorld(session.World);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        int actions = 0;
        channel.FrameSent += (frame, _) => { if (frame.Opcode == 0x002C) actions++; };
        await session.RequestDockAsync(11);
        if (gate) await session.RequestGateAsync(33, 1);
        else await session.RequestLandAsync(33);
        await Dispatch(session, 0, 22);
        await Dispatch(session, 5, 22);
        Assert.Equal(3, actions);
        Assert.Equal(new Net7TransitionRequestState(gate ? Net7TransitionKind.Gate : Net7TransitionKind.Land, 33),
            session.PlayerSemantics.TransitionRequest);
    }

    private static Task Dispatch(Net7Session session, uint message, uint target)
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload, message);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), target);
        return (Task)typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, [Packet.ForOpcode(0x0092, payload), "offline loopback", CancellationToken.None])!;
    }
}
