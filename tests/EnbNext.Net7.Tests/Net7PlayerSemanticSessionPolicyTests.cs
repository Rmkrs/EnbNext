using System.Buffers.Binary;
using System.Reflection;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Transport;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7PlayerSemanticSessionPolicyTests
{


    [Fact]
    public async Task GenericTransitionActionsCannotBypassTypedRequestCommitPolicy()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendActionAsync(Net7PlayerAction.DockRequest, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendActionAsync(Net7PlayerAction.Dock, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendActionAsync(Net7PlayerAction.LandRequest, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendActionAsync(Net7PlayerAction.Landing, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendActionAsync(Net7PlayerAction.GateRequest, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendActionAsync(Net7PlayerAction.Gate, 1));
    }

    [Fact]
    public async Task Move11ClientTransitionStateIsIndependentOfProxyForwardingGateAndUsesCameraControlTarget()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        session.World.SetLocalPlayerId(0x11223344);

        // No routed gameplay transport exists, so the request is suppressed at the
        // proxy-equivalent forwarding gate. Retail-side request state still exists.
        Assert.False(await session.RequestDockAsync(0x01020304));
        Assert.Equal(0u, session.PlayerSemantics.RequireCommitTarget(Net7TransitionKind.Dock));

        MethodInfo dispatch = typeof(Net7Session).GetMethod(
            "DispatchGameplayPacketAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task Dispatch0092(uint message, uint gameId)
        {
            byte[] payload = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), message);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), gameId);
            Task task = (Task)dispatch.Invoke(session,
                [Packet.ForOpcode(0x0092, payload), "direct outer UDP", CancellationToken.None])!;
            await task;
        }

        // With no gameplay client target, CameraControl is consumed without client mutation.
        await Dispatch0092(5, 0x55667788);
        Assert.Equal(0u, session.PlayerSemantics.RequireCommitTarget(Net7TransitionKind.Dock));

        FieldInfo transportField = typeof(Net7Session).GetField(
            "_transportState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var transport = Assert.IsType<Net7TransportState>(transportField.GetValue(session));
        transport.CommitValidatedFixedSessionId(0xAABBCCDD);
        transport.CommitGameplayPort(3501);
        transport.SetGameplayForwardingActive(true);
        typeof(Net7Session).GetField("_gameplayClientTargetActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(session, true);

        await Dispatch0092(0, 0x55667788);
        // Native 00739FF0 selector 0 queues departure event 0x0C, not clearance.
        Assert.NotNull(session.PlayerSemantics.TransitionRequest);
        Assert.Equal(0x55667788u, session.PlayerSemantics.RequireCommitTarget(Net7TransitionKind.Dock));

        await Dispatch0092(5, 0x55667788);
        // Clearance now invokes the native no-cinematic commit. The target is
        // consumed before the ordinary send gate even without a routed socket.
        Assert.Null(session.PlayerSemantics.TransitionRequest);
        Assert.Equal(uint.MaxValue, session.PlayerSemantics.RequireCommitTarget(Net7TransitionKind.Dock));

        // Camera-enabled requests retain their target for cinematic completion.
        Assert.False(await session.RequestGateAsync(0x11111111, 0));
        await Dispatch0092(4, 0x22222222);
        Assert.Equal(0x22222222u, session.PlayerSemantics.RequireCommitTarget(Net7TransitionKind.Gate));

        Assert.False(await session.RequestLandAsync(0x33333333));
        await Dispatch0092(4, 0x44444444);
        Assert.Equal(0x33333333u, session.PlayerSemantics.RequireCommitTarget(Net7TransitionKind.Land));

        byte[] cameraBytes = Convert.FromHexString("0000000411223344");
        Assert.Equal(new Net7CameraControl(4, 0x11223344), Net7GameplayWire.DecodeCameraControl(cameraBytes));
        Assert.Equal(new Net7CameraControl(4, 0x11223300), Net7GameplayWire.DecodeCameraControl(cameraBytes.AsSpan(0, 7)));
        Assert.Equal(new Net7CameraControl(0, 0), Net7GameplayWire.DecodeCameraControl(new byte[9]));
    }

    [Fact]
    public async Task WarpLeaveAndMissionPoliciesFailBeforeTransportWhenRequiredProvenanceIsAbsent()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartWarpAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.LeaveStarbaseAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ForfeitMissionAsync(0, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ClearMissionAsync(0));
    }


    [Fact]
    public async Task GateRequestOptionalVarRejectsValuesOutsideCurrentCameraDerivedDomainBeforeTransport()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.RequestGateAsync(1, 2));
    }

    [Fact]
    public async Task ConfirmedSkillTrainingDoesNotRevalidatePresentationEligibility()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });

        // Default materialized skill state is not trainable. That predicate belongs
        // to the Upgrade UI, not the result-2 serializer. With no local player yet,
        // the confirmed send must therefore reach session provenance and fail there
        // instead of silently returning false on CanTrain.
        Assert.False(session.PlayerSemantics.GetSkill(0).CanTrain);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.TrainSkillAsync(0));

        // Once the retained GameID exists, an inactive gameplay route suppresses
        // transport without manufacturing local Level/SkillPoints success.
        session.World.SetLocalPlayerId(0x11223344);
        Net7SkillView before = session.PlayerSemantics.GetSkill(0);
        Assert.False(await session.TrainSkillAsync(0));
        Net7SkillView after = session.PlayerSemantics.GetSkill(0);
        Assert.Equal(before.Level, after.Level);
        Assert.Equal(before.SkillPoints, after.SkillPoints);
    }
}
