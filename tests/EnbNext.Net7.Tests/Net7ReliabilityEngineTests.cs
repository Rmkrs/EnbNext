using System.Buffers.Binary;
using EnbNext.Net7.Reliability;
using EnbNext.Net7.Timing;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class Net7ReliabilityEngineTests
{
    [Theory]
    [InlineData(0x8000)]
    [InlineData(0xFFFF)]
    public async Task SignedHighReliabilityOuterLengthIsRejectedBeforeCacheMutation(int totalLength)
    {
        await using var h = new EngineHarness();
        h.Engine.PrepareForMasterLogin();

        int payloadLength = totalLength - ServerUdpFrame.HeaderSize;
        byte[] payload = new byte[payloadLength];
        await h.Engine.AcceptOuterAsync(OuterA(0, payload));

        Net7ReliabilitySnapshot snapshot = h.Engine.Snapshot();
        Assert.Equal(uint.MaxValue, snapshot.Cursor);
        Assert.Empty(snapshot.Entries);
        Assert.Empty(h.Sent);
        Assert.Empty(h.Ordinary);
    }

    [Fact]
    public async Task MasterLoginWaitsForSequenceZeroAnchor()
    {
        await using var h = new EngineHarness();
        h.Engine.PrepareForMasterLogin();

        await h.Engine.AcceptOuterAsync(OuterA(7, Inner(0x0001)));
        Net7ReliabilitySnapshot before = h.Engine.Snapshot();
        Assert.Equal(uint.MaxValue, before.Cursor);
        Assert.Empty(before.Entries);

        await h.Engine.AcceptOuterAsync(OuterA(0, Inner(0x0001)));
        Net7ReliabilitySnapshot after = h.Engine.Snapshot();
        Assert.Equal(1u, after.Cursor);
        AssertEntry(after, 0, Net7ReliabilityEntryState.Processed);
        Assert.Single(h.Ordinary);
    }

    [Fact]
    public async Task FirstInsertionMarkerCanMoveBackwardsAndPendingReplacementChangesVariantOnly()
    {
        await using var h = new EngineHarness(Net7FeatureFlags.SectorCaching);
        h.Engine.PrepareForMasterLogin();
        await h.Engine.AcceptOuterAsync(OuterA(0, Inner(0x0001)));
        await h.Engine.AcceptOuterAsync(OuterA(10, Inner(0x0001)));
        await h.Engine.AcceptOuterAsync(OuterA(2, Inner(0x0001)));
        Assert.Equal(2u, h.Engine.Snapshot().LastFirstSeen);

        await h.Engine.AcceptOuterAsync(new ServerUdpFrame(0x201A, Fixed, 10, Inner(0x0002)));
        Net7ReliabilitySnapshot snapshot = h.Engine.Snapshot();
        Assert.Equal(2u, snapshot.LastFirstSeen);
        Net7ReliabilityEntrySnapshot entry = AssertEntry(snapshot, 10, Net7ReliabilityEntryState.Pending);
        Assert.Equal((byte)1, entry.Variant);
        Assert.Equal((ushort)0x201A, entry.Opcode);
    }

    [Fact]
    public async Task MissingCurrentSequenceSendsSix2017AttemptsThenBecomesAbandoned()
    {
        var clock = new ImmediateClock();
        await using var h = new EngineHarness(clock: clock);
        h.Engine.PrepareForMasterLogin();
        await h.Engine.AcceptOuterAsync(OuterA(0, Inner(0x0001)));
        await h.Engine.AcceptOuterAsync(OuterA(2, Inner(0x0001)));
        await h.Engine.DrainBackgroundRecoveryAsync();

        Net7ReliabilitySnapshot snapshot = h.Engine.Snapshot();
        Assert.Equal(1u, snapshot.Cursor);
        AssertEntry(snapshot, 1, Net7ReliabilityEntryState.Abandoned);
        Assert.Equal(6, h.Sent.Count(x => x.Opcode == 0x2017));
        Assert.All(
            h.Sent.Where(x => x.Opcode == 0x2017),
            x =>
            {
                Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(x.Payload.AsSpan(0, 4)));
                Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(x.Payload.AsSpan(4, 4)));
            });
    }

    [Fact]
    public async Task GapMoreThanTwentyFiveAdvancesCursorExactlyOnceWhileRecoveryRuns()
    {
        var clock = new ImmediateClock();
        await using var h = new EngineHarness(clock: clock);
        h.Engine.PrepareForMasterLogin();
        await h.Engine.AcceptOuterAsync(OuterA(0, Inner(0x0001)));
        await h.Engine.AcceptOuterAsync(OuterA(27, Inner(0x0001)));
        await h.Engine.DrainBackgroundRecoveryAsync();

        Assert.Equal(2u, h.Engine.Snapshot().Cursor);
        AssertEntry(h.Engine.Snapshot(), 1, Net7ReliabilityEntryState.Abandoned);
    }

    [Fact]
    public async Task FirstBadDecodeRequestsReplacement_SecondBadCopyIsSkippedAsProcessed()
    {
        var clock = new BlockingClock();
        await using var h = new EngineHarness(clock: clock);
        h.Engine.PrepareForMasterLogin();
        byte[] invalid = { 2, 0, 1, 0 };

        await h.Engine.AcceptOuterAsync(OuterA(0, invalid));
        Net7ReliabilitySnapshot first = h.Engine.Snapshot();
        Assert.Equal(0u, first.Cursor);
        Assert.True(first.BadDecodeRetryMarker);
        AssertEntry(first, 0, Net7ReliabilityEntryState.RecoveryOutstanding);
        Assert.Single(h.Sent, x => x.Opcode == 0x2017);

        await h.Engine.AcceptOuterAsync(OuterA(0, invalid));
        Net7ReliabilitySnapshot second = h.Engine.Snapshot();
        Assert.Equal(1u, second.Cursor);
        Assert.False(second.BadDecodeRetryMarker);
        AssertEntry(second, 0, Net7ReliabilityEntryState.Processed);

        clock.ReleaseAll();
        await h.Engine.DrainBackgroundRecoveryAsync();
        AssertEntry(h.Engine.Snapshot(), 0, Net7ReliabilityEntryState.Processed);
    }

    [Fact]
    public async Task BadDecodeMarkerCanRemainStaleAcrossAbandonmentAndConsumesNextBadPacketAsRepeat()
    {
        await using var h = new EngineHarness(clock: new ImmediateClock());
        h.Engine.PrepareForMasterLogin();
        byte[] invalid = { 2, 0, 1, 0 };

        await h.Engine.AcceptOuterAsync(OuterA(0, invalid));
        await h.Engine.DrainBackgroundRecoveryAsync();
        Net7ReliabilitySnapshot abandoned = h.Engine.Snapshot();
        Assert.True(abandoned.BadDecodeRetryMarker);
        AssertEntry(abandoned, 0, Net7ReliabilityEntryState.Abandoned);

        await h.Engine.ProcessOneAsync(); // skip abandoned zero, marker intentionally remains set
        await h.Engine.AcceptOuterAsync(OuterA(1, invalid));

        Net7ReliabilitySnapshot next = h.Engine.Snapshot();
        Assert.False(next.BadDecodeRetryMarker);
        Assert.Equal(2u, next.Cursor);
        AssertEntry(next, 1, Net7ReliabilityEntryState.Processed);
        Assert.Equal(6, h.Sent.Count(x => x.Opcode == 0x2017)); // no new worker for sequence 1
    }

    [Fact]
    public async Task SignedNegativeInnerLengthRequestsFastRestartButOuterStillCompletes()
    {
        await using var h = new EngineHarness(clock: new ImmediateClock());
        h.Engine.PrepareForMasterLogin();
        byte[] negative = { 0x00, 0x80, 0x34, 0x12 };

        await h.Engine.AcceptOuterAsync(OuterA(0, negative));

        Net7ReliabilitySnapshot snapshot = h.Engine.Snapshot();
        Assert.Equal(1u, snapshot.Cursor);
        AssertEntry(snapshot, 0, Net7ReliabilityEntryState.Processed);
        Assert.Single(h.Sent, x => x.Opcode == 0x2022);
        Assert.DoesNotContain(h.Sent, x => x.Opcode == 0x2017);
    }

    [Fact]
    public async Task SuppressedFastRestartDoesNotCommitRestartGenerationOrEightSecondRateGate()
    {
        await using var h = new EngineHarness(clock: new ImmediateClock()) { SendSucceeds = false };
        h.Engine.PrepareForMasterLogin();

        await h.Engine.AcceptOuterAsync(OuterA(0, new byte[] { 0x00, 0x80, 0x34, 0x12 }));

        Net7ReliabilitySnapshot suppressed = h.Engine.Snapshot();
        Assert.False(suppressed.RestartRequested);
        Assert.Equal(0u, suppressed.LastRestartTick);
        Assert.DoesNotContain(h.Sent, x => x.Opcode == 0x2022);

        // This rejects stock's silent-send/state-mutation defect. Because
        // no packet was emitted, the next eligible trigger is not rate-limited.
        await h.Clock.DelayAsync(TimeSpan.FromMilliseconds(1));
        h.SendSucceeds = true;
        await h.Engine.AcceptOuterAsync(OuterA(1, new byte[] { 0x00, 0x80, 0x34, 0x12 }));

        Net7ReliabilitySnapshot emitted = h.Engine.Snapshot();
        Assert.True(emitted.RestartRequested);
        Assert.Equal(1u, emitted.LastRestartTick);
        Assert.Single(h.Sent, x => x.Opcode == 0x2022);
    }

    [Fact]
    public async Task TranslatorDeclinedHighOpcodeRequestsFastRestartAndOrdinaryBadDecodeRecovery()
    {
        await using var h = new EngineHarness(clock: new ImmediateClock());
        h.Engine.PrepareForMasterLogin();

        await h.Engine.AcceptOuterAsync(OuterA(0, Inner(0x1234)));
        await h.Engine.DrainBackgroundRecoveryAsync();

        Assert.Single(h.Sent, x => x.Opcode == 0x2022);
        Assert.Equal(6, h.Sent.Count(x => x.Opcode == 0x2017));
        AssertEntry(h.Engine.Snapshot(), 0, Net7ReliabilityEntryState.Abandoned);
    }

    [Theory]
    [InlineData(0x2010)]
    [InlineData(0x2016)]
    [InlineData(0x201A)]
    [InlineData(0x2027)]
    [InlineData(0x3005)]
    [InlineData(0x3007)]
    [InlineData(0x202C)]
    public async Task DirectOuterOnlyOpcodeInsideDecodedStreamTriggersRestartAndBadDecodeRecovery(int opcode)
    {
        await using var h = new EngineHarness(clock: new ImmediateClock());
        h.Engine.PrepareForMasterLogin();

        await h.Engine.AcceptOuterAsync(OuterA(0, Inner(checked((ushort)opcode))));
        await h.Engine.DrainBackgroundRecoveryAsync();

        Assert.Single(h.Sent, x => x.Opcode == 0x2022);
        Assert.Equal(6, h.Sent.Count(x => x.Opcode == 0x2017));
        Assert.Empty(h.Ordinary);
        AssertEntry(h.Engine.Snapshot(), 0, Net7ReliabilityEntryState.Abandoned);
    }


    [Theory]
    [InlineData(0x2012)]
    [InlineData(0x2013)]
    [InlineData(0x2014)]
    [InlineData(0x2025)]
    [InlineData(0x202A)]
    public async Task GameplayDomainSpecialsAreConsumedWithoutFalseRestartOrRetransmit(int opcode)
    {
        await using var h = new EngineHarness(
            clock: new ImmediateClock(),
            specialHandler: (packet, _) => Task.FromResult(
                Net7DecodedSpecialOwnership.IsGameplayDomainSpecial(packet.Header.Opcode)
                    ? Net7DecodedPacketDisposition.Consumed
                    : Net7DecodedPacketDisposition.Unhandled));
        h.Engine.PrepareForMasterLogin();

        await h.Engine.AcceptOuterAsync(OuterA(0, Inner(checked((ushort)opcode))));
        await h.Engine.DrainBackgroundRecoveryAsync();

        Assert.DoesNotContain(h.Sent, x => x.Opcode is 0x2022 or 0x2017);
        Assert.Empty(h.Ordinary);
        AssertEntry(h.Engine.Snapshot(), 0, Net7ReliabilityEntryState.Processed);
    }

    [Fact]
    public async Task Full05CleanDecodedPathDoesNotApplyLegacyShortAuxSuppression()
    {
        await using var h = new EngineHarness();
        h.Engine.PrepareForMasterLogin();

        await h.Engine.AcceptOuterAsync(OuterA(0, Inner(0x001B, new byte[3])));

        OrdinaryPacket packet = Assert.Single(h.Ordinary);
        Assert.Equal((ushort)0x001B, packet.Packet.Header.Opcode);
        Assert.Equal(3, packet.Packet.Payload.Length);
        Assert.Empty(h.Sent);
        AssertEntry(h.Engine.Snapshot(), 0, Net7ReliabilityEntryState.Processed);
    }


    [Fact]
    public async Task ServerPacketProcessingStopSuppressesLaterDecodedDeliveryWithoutFalseRecovery()
    {
        await using var h = new EngineHarness(clock: new ImmediateClock());
        h.Engine.PrepareForMasterLogin();
        h.Engine.RequestServerPacketProcessingStop();

        await h.Engine.AcceptOuterAsync(OuterA(0, Inner(0x0001)));
        await h.Engine.AcceptOuterAsync(OuterA(1, Inner(0x0002)));
        await h.Engine.DrainBackgroundRecoveryAsync();

        Assert.Empty(h.Ordinary);
        Assert.DoesNotContain(h.Sent, x => x.Opcode is 0x2022 or 0x2017);
        AssertEntry(h.Engine.Snapshot(), 0, Net7ReliabilityEntryState.Processed);
        AssertEntry(h.Engine.Snapshot(), 1, Net7ReliabilityEntryState.Processed);
        Assert.Equal(2u, h.Engine.Snapshot().Cursor);
    }

    [Fact]
    public async Task Decoded2020IsConsumedAndEchoedAs2021()
    {
        await using var h = new EngineHarness();
        h.Engine.PrepareForMasterLogin();
        byte[] stage = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(stage, 0x10203040);

        await h.Engine.AcceptOuterAsync(OuterA(0, Inner(0x2020, stage)));

        SentPacket sent = Assert.Single(h.Sent);
        Assert.Equal((ushort)0x2021, sent.Opcode);
        Assert.Equal(0x10203040u, BinaryPrimitives.ReadUInt32LittleEndian(sent.Payload));
        Assert.Empty(h.Ordinary);
        AssertEntry(h.Engine.Snapshot(), 0, Net7ReliabilityEntryState.Processed);
    }

    [Fact]
    public async Task Decoded202DAnd202EAreAlwaysConsumedButMutateOnlyWhenPrerequisitesPermit()
    {
        await using (var disabled = new EngineHarness())
        {
            Assert.Equal(
                Net7DecodedPacketDisposition.Consumed,
                await disabled.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202E, ReadOnlyMemory<byte>.Empty)));
            Assert.Equal(
                Net7DecodedPacketDisposition.Consumed,
                await disabled.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202D, ReadOnlyMemory<byte>.Empty)));
            Assert.False(disabled.Engine.Snapshot().ServerGateCachingEnabled);
            Assert.False(disabled.Engine.Snapshot().GateTransitionActive);
        }

        await using var enabled = new EngineHarness(Net7FeatureFlags.SectorCaching);
        Assert.Equal(
            Net7DecodedPacketDisposition.Consumed,
            await enabled.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202E, ReadOnlyMemory<byte>.Empty)));
        Assert.True(enabled.Engine.Snapshot().ServerGateCachingEnabled);
        Assert.Equal(
            Net7DecodedPacketDisposition.Consumed,
            await enabled.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202D, ReadOnlyMemory<byte>.Empty)));
        Assert.True(enabled.Engine.Snapshot().GateTransitionActive);
        Assert.True(enabled.Engine.Snapshot().GateAcknowledgementArm);
    }

    [Fact]
    public async Task SplitStarterAndVariableContinuationsReassembleOneLogicalInnerRecord()
    {
        await using var h = new EngineHarness();
        h.Engine.PrepareForMasterLogin();
        byte[] logical = InnerWithTotalLength(0x0010, 600);

        await h.Engine.AcceptOuterAsync(OuterA(0, logical[..512]));
        Net7ReliabilitySnapshot starter = h.Engine.Snapshot();
        Assert.Equal(1u, starter.Cursor);
        Assert.Equal(88, starter.ReassemblyRemaining);
        AssertEntry(starter, 0, Net7ReliabilityEntryState.Processed);

        await h.Engine.AcceptOuterAsync(new ServerUdpFrame(0x201A, Fixed, 1, logical.AsMemory(512, 40)));
        Net7ReliabilitySnapshot middle = h.Engine.Snapshot();
        Assert.Equal(2u, middle.Cursor);
        Assert.Equal(48, middle.ReassemblyRemaining);
        AssertEntry(middle, 1, Net7ReliabilityEntryState.Pending);

        await h.Engine.AcceptOuterAsync(new ServerUdpFrame(0x201A, Fixed, 2, logical.AsMemory(552, 48)));
        Net7ReliabilitySnapshot done = h.Engine.Snapshot();
        Assert.Equal(3u, done.Cursor);
        Assert.Equal(0, done.ReassemblyRemaining);
        AssertEntry(done, 1, Net7ReliabilityEntryState.Processed);
        AssertEntry(done, 2, Net7ReliabilityEntryState.Processed);
        OrdinaryPacket decoded = Assert.Single(h.Ordinary);
        Assert.Equal((ushort)0x0010, decoded.Packet.Header.Opcode);
        Assert.Equal(596, decoded.Packet.Payload.Length);
        Assert.Equal("reassembled decoded-inner", decoded.Source);
    }

    [Fact]
    public async Task SixFragment2995ByteInnerRecordReassemblesExactly()
    {
        await using var h = new EngineHarness();
        h.Engine.PrepareForMasterLogin();
        byte[] logical = InnerWithTotalLength(0x001B, 2995);

        await h.Engine.AcceptOuterAsync(OuterA(0, logical[..512]));
        uint sequence = 1;
        for (int offset = 512; offset < logical.Length; offset += 512)
        {
            int count = Math.Min(512, logical.Length - offset);
            await h.Engine.AcceptOuterAsync(new ServerUdpFrame(
                0x201A,
                Fixed,
                sequence++,
                logical.AsMemory(offset, count)));
        }

        Net7ReliabilitySnapshot done = h.Engine.Snapshot();
        Assert.Equal(6u, done.Cursor);
        Assert.Equal(0, done.ReassemblyRemaining);
        OrdinaryPacket decoded = Assert.Single(h.Ordinary);
        Assert.Equal((ushort)0x001B, decoded.Packet.Header.Opcode);
        Assert.Equal(2991, decoded.Packet.Payload.Length);
        Assert.Equal("reassembled decoded-inner", decoded.Source);
    }

    [Fact]
    public async Task FinalContinuationMayOvershootLogicalRemainderWithoutCreatingPhantomInnerRecord()
    {
        await using var h = new EngineHarness();
        h.Engine.PrepareForMasterLogin();
        byte[] logical = InnerWithTotalLength(0x0010, 600);
        byte[] final = new byte[100];
        logical.AsSpan(512, 88).CopyTo(final);
        final.AsSpan(88).Fill(0xCC);

        await h.Engine.AcceptOuterAsync(OuterA(0, logical[..512]));
        await h.Engine.AcceptOuterAsync(new ServerUdpFrame(0x201A, Fixed, 1, final));

        OrdinaryPacket decoded = Assert.Single(h.Ordinary);
        Assert.Equal((ushort)0x0010, decoded.Packet.Header.Opcode);
        Assert.Equal(596, decoded.Packet.Payload.Length);
        Assert.DoesNotContain(h.Sent, x => x.Opcode == 0x2022);
    }

    [Fact]
    public async Task LateShorterContinuationReplacementFailsClosedThroughSplitRestart()
    {
        await using var h = new EngineHarness();
        h.Engine.PrepareForMasterLogin();
        byte[] logical = InnerWithTotalLength(0x0010, 600);

        await h.Engine.AcceptOuterAsync(OuterA(0, logical[..512]));
        await h.Engine.AcceptOuterAsync(new ServerUdpFrame(0x201A, Fixed, 1, logical.AsMemory(512, 40)));

        // Replay permits a late same-key replacement behind the cursor. Make it
        // shorter than the bytes used by incremental accounting, then let seq 2
        // trigger final replay. The now-truncated positive record must
        // fail closed through split-restart ownership rather than faulting the
        // reliability worker.
        await h.Engine.AcceptOuterAsync(new ServerUdpFrame(0x201A, Fixed, 1, logical.AsMemory(512, 20)));
        await h.Engine.AcceptOuterAsync(new ServerUdpFrame(0x201A, Fixed, 2, logical.AsMemory(552, 48)));

        Net7ReliabilitySnapshot snapshot = h.Engine.Snapshot();
        Assert.Equal(3u, snapshot.Cursor);
        Assert.Equal(0, snapshot.ReassemblyRemaining);
        AssertEntry(snapshot, 1, Net7ReliabilityEntryState.Processed);
        AssertEntry(snapshot, 2, Net7ReliabilityEntryState.Processed);
        Assert.Empty(h.Ordinary);
        Assert.Single(h.Sent, x => x.Opcode == 0x2022);
    }

    [Fact]
    public async Task FreshVariantAAbandonsPreviousPartialSplitBeforeNormalClassification()
    {
        await using var h = new EngineHarness();
        h.Engine.PrepareForMasterLogin();
        byte[] logical = InnerWithTotalLength(0x0010, 600);

        await h.Engine.AcceptOuterAsync(OuterA(0, logical[..512]));
        await h.Engine.AcceptOuterAsync(OuterA(1, Inner(0x0002)));

        Net7ReliabilitySnapshot snapshot = h.Engine.Snapshot();
        Assert.Equal(2u, snapshot.Cursor);
        Assert.Equal(0, snapshot.ReassemblyRemaining);
        AssertEntry(snapshot, 1, Net7ReliabilityEntryState.Processed);
        Assert.Single(h.Ordinary);
        Assert.Equal((ushort)0x0002, h.Ordinary[0].Packet.Header.Opcode);
    }

    [Fact]
    public async Task StrayVariantBThresholdCheckCanMechanicallyStartSplit()
    {
        await using var h = new EngineHarness();
        h.Engine.PrepareForMasterLogin();
        byte[] logical = InnerWithTotalLength(0x0010, 600);

        await h.Engine.AcceptOuterAsync(new ServerUdpFrame(0x201A, Fixed, 0, logical[..512]));

        Net7ReliabilitySnapshot snapshot = h.Engine.Snapshot();
        Assert.Equal(1u, snapshot.Cursor);
        Assert.Equal(88, snapshot.ReassemblyRemaining);
        AssertEntry(snapshot, 0, Net7ReliabilityEntryState.Processed);
    }

    [Fact]
    public async Task StrayVariantBWithoutStarterThresholdIsSkippedAsProcessed()
    {
        await using var h = new EngineHarness();
        h.Engine.PrepareForMasterLogin();

        await h.Engine.AcceptOuterAsync(new ServerUdpFrame(0x201A, Fixed, 0, new byte[] { 4, 0, 1, 0 }));

        Assert.Equal(1u, h.Engine.Snapshot().Cursor);
        AssertEntry(h.Engine.Snapshot(), 0, Net7ReliabilityEntryState.Processed);
        Assert.Empty(h.Ordinary);
    }

    [Fact]
    public async Task AbandonedRetainedContinuationTriggersFastRestartAndClearsSplit()
    {
        await using var h = new EngineHarness(clock: new ImmediateClock());
        h.Engine.PrepareForMasterLogin();
        byte[] logical = InnerWithTotalLength(0x0010, 600);

        await h.Engine.AcceptOuterAsync(OuterA(0, logical[..512]));
        await h.Engine.AcceptOuterAsync(new ServerUdpFrame(0x201A, Fixed, 2, logical.AsMemory(512, 88)));
        await h.Engine.DrainBackgroundRecoveryAsync();
        AssertEntry(h.Engine.Snapshot(), 1, Net7ReliabilityEntryState.Abandoned);

        await h.Engine.ProcessOneAsync(); // skip abandoned sequence 1
        await h.Engine.ProcessOneAsync(); // sequence 2 completes and replay sees abandoned sequence 1

        Net7ReliabilitySnapshot snapshot = h.Engine.Snapshot();
        Assert.Equal(0, snapshot.ReassemblyRemaining);
        Assert.Contains(h.Sent, x => x.Opcode == 0x2022);
    }

    [Theory]
    [InlineData(7, false, true)]
    [InlineData(8, false, true)]
    [InlineData(0x12, true, true)]
    [InlineData(0x13, false, true)]
    [InlineData(0x1C, true, false)]
    [InlineData(0x1D, true, false)]
    public async Task GateActionSubtypesMutateOnlyTheirClosedC7C8EquivalentState(
        int subtype,
        bool expectedTransition,
        bool expectedArm)
    {
        await using var h = new EngineHarness(Net7FeatureFlags.SectorCaching);
        await h.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202E, ReadOnlyMemory<byte>.Empty));

        h.Engine.ObserveGateActionSubtype(checked((byte)subtype));

        Net7ReliabilitySnapshot snapshot = h.Engine.Snapshot();
        Assert.Equal(expectedTransition, snapshot.GateTransitionActive);
        Assert.Equal(expectedArm, snapshot.GateAcknowledgementArm);
    }

    [Fact]
    public async Task LowSequenceGateResetReanchorsGenerationOnlyAfterCursorHasAdvancedPastFifteen()
    {
        await using var h = new EngineHarness(Net7FeatureFlags.SectorCaching);
        await h.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202E, ReadOnlyMemory<byte>.Empty));
        h.Engine.PrepareForMasterLogin();

        for (uint sequence = 0; sequence < 16; sequence++)
            await h.Engine.AcceptOuterAsync(OuterA(sequence, Inner(0x0001)));
        for (int i = 0; i < 16; i++)
            await h.Engine.ProcessOneAsync();
        Assert.Equal(16u, h.Engine.Snapshot().Cursor);

        h.Engine.ObserveGateActionSubtype(0x1C); // server-enabled action activates C7-equivalent without C8
        await h.Engine.AcceptOuterAsync(OuterA(5, Inner(0x0002)));

        Net7ReliabilitySnapshot reset = h.Engine.Snapshot();
        Assert.Equal(0u, reset.Cursor);
        Assert.True(reset.GateTransitionActive);
        Assert.True(reset.GateSequenceScanMode);
        Net7ReliabilityEntrySnapshot only = Assert.Single(reset.Entries);
        Assert.Equal(5u, only.Sequence);
        Assert.Equal(Net7ReliabilityEntryState.Pending, only.State);
    }

    [Fact]
    public async Task StickyGateBoundaryCannotBypassMissingCurrentCursor()
    {
        var clock = new BlockingClock();
        await using var h = new EngineHarness(Net7FeatureFlags.SectorCaching, clock);
        await h.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202E, ReadOnlyMemory<byte>.Empty));
        await h.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202D, ReadOnlyMemory<byte>.Empty));
        h.Engine.PrepareForMasterLogin();

        await h.Engine.AcceptOuterAsync(OuterA(0, Inner(0x0001)));
        await h.Engine.AcceptOuterAsync(OuterA(1, Inner(0x202B)));
        await h.Engine.ProcessOneAsync();
        await h.Engine.ProcessOneAsync();

        Net7ReliabilitySnapshot sticky = h.Engine.Snapshot();
        Assert.Equal(1u, sticky.GateBoundarySequence);
        Assert.Equal(2u, sticky.Cursor);

        // Sequence 3 proves there is later data while current sequence 2 is absent.
        // The ordinary current-cursor admission/recovery decision must happen
        // before sticky boundary 1 is substituted for the marker scan.
        await h.Engine.AcceptOuterAsync(OuterA(3, Inner(0x0001)));
        await h.Engine.ProcessOneAsync();

        Net7ReliabilitySnapshot missing = h.Engine.Snapshot();
        Assert.Equal(2u, missing.Cursor);
        AssertEntry(missing, 2, Net7ReliabilityEntryState.RecoveryOutstanding);
        Assert.True(clock.HasBlockedDelay);
    }

    [Fact]
    public async Task GateMarkerAcknowledgementAndOppositeSideLoginReleaseTransitionDeterministically()
    {
        await using var h = new EngineHarness(Net7FeatureFlags.SectorCaching);
        await h.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202E, ReadOnlyMemory<byte>.Empty));
        await h.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202D, ReadOnlyMemory<byte>.Empty));
        h.Engine.PrepareForMasterLogin();

        await h.Engine.AcceptOuterAsync(OuterA(0, Inner(0x0001)));
        await h.Engine.AcceptOuterAsync(OuterA(1, Inner(0x0001)));
        await h.Engine.AcceptOuterAsync(OuterA(2, Inner(0x0001)));
        await h.Engine.AcceptOuterAsync(OuterA(3, Inner(0x202B)));
        await h.Engine.ProcessOneAsync();
        await h.Engine.ProcessOneAsync();
        await h.Engine.ProcessOneAsync();
        await h.Engine.ProcessOneAsync();

        Net7ReliabilitySnapshot ready = h.Engine.Snapshot();
        Assert.Equal(3u, ready.GateBoundarySequence);
        Assert.True(ready.GateAcknowledgementLatch);
        Assert.Contains(h.Sent, x => x.Opcode == 0x202C && x.Payload.SequenceEqual(new byte[4]));

        await h.Engine.ProcessGateHandoffAsync();
        Assert.True(h.Engine.Snapshot().GateProcessingPaused);
        Assert.False(h.Engine.Snapshot().GateAcknowledgementArm);

        Assert.True(h.Engine.TryConsumeOutgoingGameplayLogin(0x11223344));
        await h.Engine.ProcessGateHandoffAsync();
        Net7ReliabilitySnapshot released = h.Engine.Snapshot();
        Assert.False(released.GateTransitionActive);
        Assert.False(released.GateProcessingPaused);
        Assert.True(released.PostGateTransitionByte);
        Assert.Equal(1u, released.Cursor);

        byte[] setTime = new byte[12];
        Packet rewritten = h.Engine.ApplySavedGateTimeIfNeeded(Packet.ForOpcode(0x0034, setTime));
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(rewritten.Payload.Span[..4]));
        Assert.Equal(0u, h.Engine.Snapshot().SavedClientTime);
    }

    [Fact]
    public async Task MalformedDecodedInnerSetClientTimeStillConsumesSavedGateTime()
    {
        await using var h = new EngineHarness(Net7FeatureFlags.SectorCaching);
        await h.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202E, ReadOnlyMemory<byte>.Empty));
        await h.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202D, ReadOnlyMemory<byte>.Empty));
        Assert.True(h.Engine.TryConsumeOutgoingGameplayLogin(0x11223344));

        Packet malformed = Packet.ForOpcode(0x0034, new byte[2]);
        Packet unchanged = h.Engine.ApplySavedGateTimeIfNeeded(malformed);

        Assert.Equal(malformed.Payload.ToArray(), unchanged.Payload.ToArray());
        Assert.Equal(0u, h.Engine.Snapshot().SavedClientTime);
    }

    [Fact]
    public async Task DirectOuterSetClientTimeConsumesSavedGateTimeWithoutRewritingVisibleBytes()
    {
        await using var h = new EngineHarness(Net7FeatureFlags.SectorCaching);
        await h.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202E, ReadOnlyMemory<byte>.Empty));
        await h.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202D, ReadOnlyMemory<byte>.Empty));
        Assert.True(h.Engine.TryConsumeOutgoingGameplayLogin(0x11223344));

        byte[] setTime = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(setTime.AsSpan(0, 4), 0xAABBCCDD);
        h.Engine.ConsumeSavedGateTimeWithoutRewrite(0x0034);

        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32LittleEndian(setTime.AsSpan(0, 4)));
        Assert.Equal(0u, h.Engine.Snapshot().SavedClientTime);
    }

    [Fact]
    public async Task GateAcknowledgementLatchSuppressesLaterScannerInvocationButNotAdditionalMarkersInSameScan()
    {
        await using var h = new EngineHarness(Net7FeatureFlags.SectorCaching);
        await h.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202E, ReadOnlyMemory<byte>.Empty));
        await h.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202D, ReadOnlyMemory<byte>.Empty));
        h.Engine.PrepareForMasterLogin();

        await h.Engine.AcceptOuterAsync(OuterA(0, Inner(0x0001)));
        byte[] twoMarkers = Inner(0x202B).Concat(Inner(0x202B)).ToArray();
        await h.Engine.AcceptOuterAsync(OuterA(1, twoMarkers));
        await h.Engine.AcceptOuterAsync(OuterA(2, Inner(0x202B)));

        await h.Engine.ProcessOneAsync(); // retained sequence 0, no marker
        await h.Engine.ProcessOneAsync(); // both markers are scanned in this one invocation

        Assert.Equal(2, h.Sent.Count(x => x.Opcode == 0x202C));
        Assert.True(h.Engine.Snapshot().GateAcknowledgementLatch);

        await h.Engine.ProcessOneAsync(); // scanner entry sees the already-set latch and returns
        Assert.Equal(2, h.Sent.Count(x => x.Opcode == 0x202C));
    }

    [Fact]
    public async Task HandoffClearsReliabilityOutsideGateButPreservesItDuringGateTransition()
    {
        await using (var ordinary = new EngineHarness(Net7FeatureFlags.SectorCaching))
        {
            ordinary.Engine.PrepareForMasterLogin();
            await ordinary.Engine.AcceptOuterAsync(OuterA(0, Inner(0x0001)));
            Assert.NotEmpty(ordinary.Engine.Snapshot().Entries);
            ordinary.Engine.HandleServerHandoff();
            Assert.Equal(uint.MaxValue, ordinary.Engine.Snapshot().Cursor);
            Assert.Empty(ordinary.Engine.Snapshot().Entries);
        }

        await using var gated = new EngineHarness(Net7FeatureFlags.SectorCaching);
        await gated.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202E, ReadOnlyMemory<byte>.Empty));
        await gated.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202D, ReadOnlyMemory<byte>.Empty));
        gated.Engine.PrepareForMasterLogin();
        await gated.Engine.AcceptOuterAsync(OuterA(0, Inner(0x0001)));
        gated.Engine.HandleServerHandoff();
        Assert.NotEmpty(gated.Engine.Snapshot().Entries);
        Assert.Equal(0u, gated.Engine.Snapshot().Cursor);
    }

    [Fact]
    public async Task DisposeCancelsABlockedRecoveryWorkerInsteadOfWaitingForThreeSecondBudget()
    {
        var clock = new BlockingClock();
        var h = new EngineHarness(clock: clock);
        h.Engine.PrepareForMasterLogin();
        await h.Engine.AcceptOuterAsync(OuterA(0, new byte[] { 2, 0, 1, 0 }));
        Assert.True(clock.HasBlockedDelay);

        Task disposal = h.Engine.DisposeAsync().AsTask();
        Assert.True(clock.CancellationObserved);
        // The fake clock never advances: completion proves cancellation, not
        // a wall-clock race against a loaded test runner.
        await disposal.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private const uint Fixed = 0xAABBCCDD;

    private static ServerUdpFrame OuterA(uint sequence, ReadOnlyMemory<byte> payload)
        => new(0x2016, Fixed, sequence, payload);

    private static byte[] Inner(ushort opcode)
        => Inner(opcode, ReadOnlySpan<byte>.Empty);

    private static byte[] Inner(ushort opcode, ReadOnlySpan<byte> payload)
    {
        byte[] result = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0, 2), checked((ushort)result.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2, 2), opcode);
        payload.CopyTo(result.AsSpan(4));
        return result;
    }

    private static byte[] InnerWithTotalLength(ushort opcode, int totalLength)
    {
        if (totalLength < 4 || totalLength > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(totalLength));
        byte[] result = new byte[totalLength];
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0, 2), checked((ushort)totalLength));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2, 2), opcode);
        for (int i = 4; i < result.Length; i++)
            result[i] = unchecked((byte)i);
        return result;
    }

    private static Net7ReliabilityEntrySnapshot AssertEntry(
        Net7ReliabilitySnapshot snapshot,
        uint sequence,
        Net7ReliabilityEntryState state)
    {
        Net7ReliabilityEntrySnapshot entry = Assert.Single(snapshot.Entries, x => x.Sequence == sequence);
        Assert.Equal(state, entry.State);
        return entry;
    }

    private sealed record SentPacket(ushort Opcode, byte[] Payload);
    private sealed record OrdinaryPacket(Packet Packet, string Source);

    [Fact]
    public async Task Move11MalformedCachedSequenceZeroDoesNotEnterPausedRendezvous()
    {
        await using var h = new EngineHarness(Net7FeatureFlags.SectorCaching);
        await h.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202E, Array.Empty<byte>()));
        h.Engine.ObserveGateActionSubtype(0x1C);
        h.Engine.ObserveGateActionSubtype(0x07);

        await h.Engine.AcceptOuterAsync(OuterA(0, new byte[] { 0x02, 0x00, 0x01, 0x00 }));
        await h.Engine.AcceptOuterAsync(OuterA(1, Inner(0x202B)));
        await h.Engine.ProcessOneAsync();
        await h.Engine.ProcessOneAsync();

        Net7ReliabilitySnapshot ready = h.Engine.Snapshot();
        Assert.True(ready.GateAcknowledgementLatch);
        Assert.True(ready.GateAcknowledgementArm);
        Assert.False(ready.GateProcessingPaused);
        Assert.Contains(h.Sent, x => x.Opcode == 0x202C);

        await h.Engine.ProcessGateHandoffAsync();

        Net7ReliabilitySnapshot rejected = h.Engine.Snapshot();
        Assert.True(rejected.GateTransitionActive);
        Assert.True(rejected.GateAcknowledgementLatch);
        Assert.True(rejected.GateAcknowledgementArm);
        Assert.False(rejected.GateProcessingPaused);
        Assert.Empty(h.Ordinary);
    }

    [Fact]
    public async Task Move11TransitionOwnershipResetPreservesCapabilityButCancelsStaleRendezvous()
    {
        await using var h = new EngineHarness(Net7FeatureFlags.SectorCaching);
        await h.Engine.HandleReliabilitySpecialAsync(Packet.ForOpcode(0x202E, Array.Empty<byte>()));
        h.Engine.ObserveGateActionSubtype(0x12);
        Assert.True(h.Engine.Snapshot().ServerGateCachingEnabled);
        Assert.True(h.Engine.Snapshot().GateTransitionActive);
        Assert.True(h.Engine.Snapshot().GateAcknowledgementArm);

        h.Engine.ResetGateTransitionOwnership();
        Net7ReliabilitySnapshot reset = h.Engine.Snapshot();
        Assert.True(reset.ServerGateCachingEnabled);
        Assert.False(reset.GateTransitionActive);
        Assert.False(reset.GateAcknowledgementArm);
        Assert.False(reset.GateProcessingPaused);
        Assert.False(reset.GateAcknowledgementLatch);
        Assert.False(h.Engine.TryConsumeOutgoingGameplayLogin(1234));

        await using var noCache = new EngineHarness();
        noCache.Engine.ObserveGateActionSubtype(0x07);
        Assert.True(noCache.Engine.Snapshot().GateAcknowledgementArm);
        noCache.Engine.HandleServerHandoff();
        Assert.False(noCache.Engine.Snapshot().GateAcknowledgementArm);
    }

    private sealed class EngineHarness : IAsyncDisposable
    {
        public EngineHarness(
            Net7FeatureFlags flags = Net7FeatureFlags.None,
            INet7RuntimeClock? clock = null,
            Func<Packet, CancellationToken, Task<Net7DecodedPacketDisposition>>? specialHandler = null)
        {
            Clock = clock ?? new ImmediateClock();
            Engine = new Net7ReliabilityEngine(
                new Net7ServerOptions { FeatureFlags = flags },
                Clock,
                () => Active,
                (opcode, payload, _) =>
                {
                    if (!SendSucceeds)
                        return Task.FromResult(false);
                    Sent.Add(new SentPacket(opcode, payload.ToArray()));
                    return Task.FromResult(true);
                },
                specialHandler ?? ((_, _) => Task.FromResult(Net7DecodedPacketDisposition.Unhandled)),
                (packet, source, _) =>
                {
                    Ordinary.Add(new OrdinaryPacket(packet, source));
                    return Task.CompletedTask;
                });
        }

        public INet7RuntimeClock Clock { get; }
        public Net7ReliabilityEngine Engine { get; }
        public List<SentPacket> Sent { get; } = [];
        public List<OrdinaryPacket> Ordinary { get; } = [];
        public bool Active { get; set; } = true;
        public bool SendSucceeds { get; set; } = true;
        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    private sealed class ImmediateClock : INet7RuntimeClock
    {
        private uint _now;
        public uint GetMilliseconds() => _now;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _now = unchecked(_now + checked((uint)Math.Max(0, (long)delay.TotalMilliseconds)));
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingClock : INet7RuntimeClock
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _released;
        public bool HasBlockedDelay { get; private set; }
        public bool CancellationObserved { get; private set; }
        public uint GetMilliseconds() => 0;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            if (_released)
                return Task.CompletedTask;
            HasBlockedDelay = true;
            cancellationToken.Register(() => CancellationObserved = true);
            return _release.Task.WaitAsync(cancellationToken);
        }

        public void ReleaseAll()
        {
            _released = true;
            _release.TrySetResult(true);
        }
    }
}
