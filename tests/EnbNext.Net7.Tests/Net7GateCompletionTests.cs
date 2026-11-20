using System.Buffers.Binary;
using System.Reflection;
using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7GateCompletionTests
{
    [Fact]
    public async Task EachDisabledCameraUpdateSuppliesAnotherCommitTarget()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        EnbR11FlightTests.FlightWorld(session.World);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        var actions = new List<byte[]>();
        channel.FrameSent += (frame, _) => { if (frame.Opcode == 0x002C) actions.Add(frame.Payload.ToArray()); };

        await Dispatch(session, 4, 0x22222222); // No request: unsolicited camera packet.
        Assert.Empty(actions);
        Assert.True(await session.RequestGateAsync(0x11111111, 1));
        Assert.Single(actions); // Clicking alone must not commit.
        await Dispatch(session, 0, 0x33333333); // A dock packet cannot finish a gate.
        Assert.Single(actions);
        await Dispatch(session, 4, 0x22222222);
        Assert.Equal(2, actions.Count);
        Assert.Equal(Net7GameplayWire.BuildAction(7, Net7PlayerAction.Gate, 0x22222222, 0), actions[1]);
        Assert.Null(session.PlayerSemantics.TransitionRequest);
        await Dispatch(session, 4, 0x22222222);
        Assert.Equal(3, actions.Count);
        Assert.Equal(actions[1], actions[2]);
    }

    [Theory]
    [InlineData(0u, false, 0x22222222u)] // Enabled cinematic owns completion.
    [InlineData(1u, true, 0x22222222u)] // Retail incapacitation guard.
    [InlineData(1u, false, uint.MaxValue)] // Retail empty-target sentinel.
    public async Task CameraOrAvatarPolicyKeepsCommitPending(uint cameraOption, bool incapacitated, uint target)
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
        Assert.True(await session.RequestGateAsync(0x11111111, cameraOption));
        await Dispatch(session, 4, target);
        Assert.Equal(1, actions);
        Assert.Equal(target, session.PlayerSemantics.RequireCommitTarget(Net7TransitionKind.Gate));
    }

    [Fact]
    public async Task GateCameraResponseStillCommitsAfterLocalLandRequest()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        EnbR11FlightTests.FlightWorld(session.World);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        int actions = 0;
        channel.FrameSent += (frame, _) => { if (frame.Opcode == 0x002C) actions++; };
        await session.RequestGateAsync(11, 1);
        await session.RequestLandAsync(33);
        await Dispatch(session, 4, 22);
        Assert.Equal(3, actions);
        Assert.Equal(33u, session.PlayerSemantics.RequireCommitTarget(Net7TransitionKind.Land));
    }

    [Fact]
    public async Task EnabledCameraCommitsOnlyItsCurrentServerAuthorizedSequence()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        EnbR11FlightTests.FlightWorld(session.World);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        int actions = 0;
        channel.FrameSent += (frame, _) => { if (frame.Opcode == 0x002C) actions++; };
        using var manualSend=new CancellationTokenSource();
        await session.RequestGateAsync(11, 0, manualSend.Token);
        manualSend.Cancel(); // Ordinary request-send cancellation does not own the manual camera lifetime.
        await Dispatch(session, 4, 22);
        var camera = Assert.IsType<Net7GateCameraTransition>(session.Gameplay.GateCamera);
        Assert.False(await session.CompleteGateCameraAsync(camera.Sequence + 1, 22));
        Assert.False(await session.CompleteGateCameraAsync(camera.Sequence, 11));
        Assert.Equal(1, actions);
        Assert.True(await session.CompleteGateCameraAsync(camera.Sequence, 22));
        Assert.False(await session.CompleteGateCameraAsync(camera.Sequence, 22));
        Assert.Equal(2, actions);
        session.Gameplay.ResetForServerHandoff();
        Assert.Null(session.Gameplay.GateCamera);
    }

    [Theory]
    [InlineData(0u, 12)]
    [InlineData(1u, 13)]
    [InlineData(4u, 6)]
    [InlineData(5u, 10)]
    public async Task EachCameraUpdateQueuesExactlyOneNativeEvent(uint selector, int nativeEvent)
    {
        // 00739FF0 queues once; creating the presentation transition must not queue again.
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        EnbR11FlightTests.FlightWorld(session.World);
        var (_, _, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        if (selector == 4) await session.RequestGateAsync(11, 0);
        if (selector == 5) await session.RequestDockAsync(11, completeWithoutCamera: false);
        int before = session.Gameplay.NativeCameraEvents.Count;
        await Dispatch(session, selector, 22);
        Assert.Equal(before + 1, session.Gameplay.NativeCameraEvents.Count);
        Assert.Equal(nativeEvent, session.Gameplay.NativeCameraEvents[^1].Event);
        await Dispatch(session, selector, 22);
        Assert.Equal(before + 2, session.Gameplay.NativeCameraEvents.Count);
    }

    [Theory]
    [InlineData(4u)]
    [InlineData(5u)]
    public async Task ControllerReceivesUnsolicitedClearanceBeforeAnyRequest(uint selector)
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" }, useCameraController: true);
        EnbR11FlightTests.FlightWorld(session.World);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        var actions = new List<byte[]>();
        channel.FrameSent += (frame, _) => { if (frame.Opcode == 0x002C) actions.Add(frame.Payload.ToArray()); };
        await Dispatch(session, selector, 22);
        Assert.Empty(actions);
        if (selector == 4)
        {
            var camera = Assert.IsType<Net7GateCameraTransition>(session.Gameplay.GateCamera);
            Assert.True(await session.CompleteGateCameraAsync(camera.Sequence, camera.TargetId));
        }
        else
        {
            var camera = Assert.IsType<Net7DockCameraTransition>(session.Gameplay.DockCamera);
            Assert.True(await session.CompleteDockCameraAsync(camera.Revision, camera.TargetId));
        }
        Assert.Equal(Net7GameplayWire.BuildAction(7, selector == 4 ? Net7PlayerAction.Gate : Net7PlayerAction.Dock, 22, 0), Assert.Single(actions));
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
