using System.Buffers.Binary;
using EnbNext.Net7;
using EnbNext.Net7.World;

namespace EnbNext.Net7.Tests;

public sealed class Net7ClientTimeStateTests
{
    [Fact]
    public void ShortTimePacketsPreserveClockStateAndFollowingCompletePacketApplies()
    {
        for (int length = 0; length < 12; length++)
        {
            var world = new Net7WorldState();
            byte[] complete = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(complete.AsSpan(0), 100);
            BinaryPrimitives.WriteUInt32LittleEndian(complete.AsSpan(4), 120);
            BinaryPrimitives.WriteUInt32LittleEndian(complete.AsSpan(8), 125);
            world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0034, complete), 150);
            world.Time.ConvertAndObserveDrift(100, 160);
            long correction = world.Time.CorrectionMilliseconds;
            long drift = world.Time.DriftAccumulator;
            int samples = world.Time.DriftSampleCount;
            var result = world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0034, complete[..length]), 900);
            Assert.Null(result.AuxDataWarning);
            Assert.Equal(correction, world.Time.CorrectionMilliseconds);
            Assert.Equal(-5, world.Time.PatchedRttMilliseconds);
            Assert.Equal(drift, world.Time.DriftAccumulator);
            Assert.Equal(samples, world.Time.DriftSampleCount);
            world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0034, complete), 170);
            Assert.Equal(-12, world.Time.CorrectionMilliseconds);
            Assert.Equal(-5, world.Time.PatchedRttMilliseconds);
            Assert.Equal(0, world.Time.DriftAccumulator);
            Assert.Equal(0, world.Time.DriftSampleCount);
        }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void SetClientTimeUsesCurrentPatchedRttAndUnpatchedCorrectionArm(int trailingBytes)
    {
        var state = new Net7ClientTimeState();
        byte[] payload = new byte[12 + trailingBytes];
        payload.AsSpan(12).Fill(0xff);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), 120);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), 125);

        state.ApplySetClientTime(payload, 150);

        Assert.Equal(-5, state.PatchedRttMilliseconds);
        Assert.Equal(-2, state.CorrectionMilliseconds); // ((120+125)-(100+150))/2 = -2 trunc toward zero
        Assert.Equal(0, state.DriftAccumulator);
        Assert.Equal(0, state.DriftSampleCount);
        Assert.Equal(1002u, state.ConvertServerTimestamp(1000));
    }

    [Fact]
    public void DriftAdjustsOnlyAfterThresholdAndMoreThan64Samples()
    {
        var state = new Net7ClientTimeState();
        byte[] payload = new byte[12];
        state.ApplySetClientTime(payload, 0);

        for (int i = 0; i < 64; i++)
            state.ConvertAndObserveDrift(0, 2000);
        Assert.NotEqual(0, state.DriftAccumulator);
        Assert.Equal(64, state.DriftSampleCount);

        state.ConvertAndObserveDrift(0, 2000);
        Assert.Equal(0, state.DriftAccumulator);
        Assert.Equal(0, state.DriftSampleCount);
        Assert.NotEqual(0, state.CorrectionMilliseconds);
    }

    [Fact]
    public void RequestTimeCountersFireIndependentlyOnThirtyThirdQualifyingUpdate()
    {
        var state = new Net7ClientTimeState();
        for (int i = 0; i < 32; i++)
        {
            Assert.False(state.ObserveQualifyingLocalAdvancedUpdate());
            Assert.False(state.ObserveQualifyingLocalFormationUpdate());
        }
        Assert.True(state.ObserveQualifyingLocalAdvancedUpdate());
        Assert.Equal(0, state.AdvancedRequestTimeCounter);
        Assert.Equal(32, state.FormationRequestTimeCounter);
        Assert.True(state.ObserveQualifyingLocalFormationUpdate());
        Assert.Equal(0, state.FormationRequestTimeCounter);
    }

    [Fact]
    public void HandoffResetsCorrectionAndDriftButPreservesRttAndPeriodicCounters()
    {
        var state = new Net7ClientTimeState();
        byte[] payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), 110);
        state.ApplySetClientTime(payload, 50);
        _ = state.ObserveQualifyingLocalAdvancedUpdate();
        _ = state.ObserveQualifyingLocalFormationUpdate();
        long rtt = state.PatchedRttMilliseconds;

        state.ResetForServerHandoff();

        Assert.Equal(0, state.CorrectionMilliseconds);
        Assert.Equal(0, state.DriftAccumulator);
        Assert.Equal(0, state.DriftSampleCount);
        Assert.Equal(rtt, state.PatchedRttMilliseconds);
        Assert.Equal(1, state.AdvancedRequestTimeCounter);
        Assert.Equal(1, state.FormationRequestTimeCounter);
    }
    [Fact]
    public void ProcessOwnedRttAndRequestTimeCountersSurviveCharacterSelectionAndSessionReplacement()
    {
        var process = new Net7ProcessCompatibilityState();
        var first = new Net7ClientTimeState(process);
        byte[] payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), 110);
        first.ApplySetClientTime(payload, 50);
        for (int i = 0; i < 7; i++)
            Assert.False(first.ObserveQualifyingLocalAdvancedUpdate());
        for (int i = 0; i < 11; i++)
            Assert.False(first.ObserveQualifyingLocalFormationUpdate());

        long rtt = first.PatchedRttMilliseconds;
        first.ResetForCharacterSelection();
        Assert.Equal(rtt, first.PatchedRttMilliseconds);
        Assert.Equal(7, first.AdvancedRequestTimeCounter);
        Assert.Equal(11, first.FormationRequestTimeCounter);

        var replacement = new Net7ClientTimeState(process);
        Assert.Equal(rtt, replacement.PatchedRttMilliseconds);
        Assert.Equal(7, replacement.AdvancedRequestTimeCounter);
        Assert.Equal(11, replacement.FormationRequestTimeCounter);
    }

}
