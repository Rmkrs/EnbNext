using System.Buffers.Binary;
using EnbNext.Assets;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z13CurrentRetailRslAnimationExecutionTests
{
    [Fact]
    public void HAnimNameConstructionMatchesCurrentRetailThreeWayBranch()
    {
        Assert.Equal("._IDLE", EnbCurrentRetailRslAnimationRuntime.BuildHAnimName("", "", "_IDLE"));
        Assert.Equal(".OPEN", EnbCurrentRetailRslAnimationRuntime.BuildHAnimName("", "OPEN", null));
        Assert.Equal("COMMON.OPEN", EnbCurrentRetailRslAnimationRuntime.BuildHAnimName("", "COMMON.OPEN", null));
        Assert.Throws<ArgumentNullException>(() => EnbCurrentRetailRslAnimationRuntime.BuildHAnimName(null!, "OPEN", null));
        Assert.Equal(
            "TSG02.TSG02_IDLE",
            EnbCurrentRetailRslAnimationRuntime.BuildHAnimName("TSG02", string.Empty, "_IDLE"));
        Assert.Equal(
            "TSG02.OPEN",
            EnbCurrentRetailRslAnimationRuntime.BuildHAnimName("TSG02", "OPEN", "_IGNORED"));
        Assert.Equal(
            "COMMON.OPEN",
            EnbCurrentRetailRslAnimationRuntime.BuildHAnimName("TSG02", "COMMON.OPEN", "_IGNORED"));
    }

    [Fact]
    public void ClientTimeExecutionRetainsFractionalFramesLoopsAtNumFramesAndClampsStop()
    {
        EnbW3dRenderHierarchyAnimation animation = Animation(numFrames: 10, frameRate: 2);
        EnbRslStateFlags looping = Flags();

        Assert.True(EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(
            animation,
            looping,
            activationClientTime: 1000,
            currentClientTime: 2250,
            out float fractional,
            out string? diagnostic), diagnostic);
        Assert.Equal(2.5f, fractional);

        Assert.True(EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(
            animation,
            looping,
            activationClientTime: 1000,
            currentClientTime: 5750,
            out float terminalBand,
            out diagnostic), diagnostic);
        Assert.Equal(9.5f, terminalBand);

        Assert.True(EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(
            animation,
            looping,
            activationClientTime: 1000,
            currentClientTime: 6000,
            out float wrapped,
            out diagnostic), diagnostic);
        Assert.Equal(0f, wrapped);

        Assert.True(EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(
            animation,
            Flags(stop: true),
            activationClientTime: 1000,
            currentClientTime: 6000,
            out float stopped,
            out diagnostic), diagnostic);
        Assert.Equal(9f, stopped);

        Assert.True(EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(
            animation,
            Flags(stopOnce: true),
            activationClientTime: 1000,
            currentClientTime: 6000,
            out float stoppedOnce,
            out diagnostic), diagnostic);
        Assert.Equal(9f, stoppedOnce);
    }

    [Fact]
    public void ClientTimeExecutionPreservesUintTimerWraparound()
    {
        EnbW3dRenderHierarchyAnimation animation = Animation(numFrames: 10, frameRate: 2);
        uint activation = uint.MaxValue - 500;
        uint current = 499;

        Assert.True(EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(
            animation,
            Flags(),
            activation,
            current,
            out float frame,
            out string? diagnostic), diagnostic);
        Assert.Equal(2f, frame);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void LocalTransitionBearingStatesRemainExplicitlyBlocked(
        bool delete,
        bool deactivate,
        bool activateNext,
        bool activateNextOnce)
    {
        EnbW3dRenderHierarchyAnimation animation = Animation(numFrames: 10, frameRate: 2);
        EnbRslStateFlags flags = Flags(
            delete: delete,
            deactivate: deactivate,
            activateNext: activateNext,
            activateNextOnce: activateNextOnce);

        Assert.False(EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(
            animation,
            flags,
            activationClientTime: 1000,
            currentClientTime: 1100,
            out _,
            out string? diagnostic));
        Assert.Contains("transition semantics", diagnostic ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderStateActivateClientTimeSurvivesWorldAndPresentationBoundary()
    {
        var world = new Net7WorldState();
        _ = world.MaterializeCreate(new Net7CreateUpdate(0x1234, 1f, 48, 0x25, 0f, 0f, 0f));

        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), 0x1234);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), 1);
        world.Apply(Packet.ForOpcode(0x0030, payload), currentClientTime: 42_125);

        Net7RenderStateAction action = Assert.Single(world.Objects.FindFirst(0x1234)!.RenderStateActions);
        Assert.Equal((ushort)0x0030, action.Opcode);
        Assert.Equal(1u, action.RenderStateId);
        Assert.Equal(42_125u, action.ClientTime);

        var builder = new EnbPresentationSnapshotBuilder();
        EnbSceneSnapshot snapshot = builder.Build(world, assets: null, clientTime: 50_000);
        EnbSceneRenderStateActionSnapshot captured = Assert.Single(
            Assert.Single(snapshot.Objects, static obj => obj.GameId == 0x1234).RenderStateActions);
        Assert.Equal(42_125u, captured.ClientTime);
    }

    [Fact]
    public void AuthoredFlagMaskMatchesCurrentRetailAnimationParserBits()
    {
        EnbRslStateFlags flags = Flags(
            delete: true,
            deactivate: true,
            stop: true,
            activateNext: true,
            enabled: true,
            activateNextOnce: true,
            stopOnce: true,
            precached: true,
            instanceData: true,
            removePrototype: true,
            stationary: true,
            startsEnabled: true,
            dontStopSound: true,
            dontLoopSound: true);

        Assert.Equal(0x0007_4D7Fu, EnbCurrentRetailRslAnimationRuntime.EncodeAuthoredFlags(flags));
    }

    private static EnbW3dRenderHierarchyAnimation Animation(uint numFrames, uint frameRate)
        => new(
            Ordinal: 0,
            Version: 0x0003_0000u,
            Name: "TSG02.OPEN",
            HierarchyName: "TSG02",
            NumFrames: numFrames,
            FrameRate: frameRate,
            HasLegacySyntheticRootChannelShift: false,
            Channels: Array.Empty<EnbW3dRenderAnimationChannel>(),
            BitChannels: Array.Empty<EnbW3dRenderAnimationBitChannel>());

    private static EnbRslStateFlags Flags(
        bool delete = false,
        bool deactivate = false,
        bool stop = false,
        bool activateNext = false,
        bool enabled = false,
        bool removePrototype = false,
        bool precached = false,
        bool instanceData = false,
        bool activateNextOnce = false,
        bool stopOnce = false,
        bool stationary = false,
        bool startsEnabled = false,
        bool dontStopSound = false,
        bool dontLoopSound = false)
        => new(
            Delete: delete,
            Deactivate: deactivate,
            Stop: stop,
            ActivateNext: activateNext,
            Enabled: enabled,
            RemovePrototype: removePrototype,
            Precached: precached,
            InstanceData: instanceData,
            ActivateNextOnce: activateNextOnce,
            StopOnce: stopOnce,
            Stationary: stationary,
            StartsEnabled: startsEnabled,
            DontStopSound: dontStopSound,
            DontLoopSound: dontLoopSound);
}
