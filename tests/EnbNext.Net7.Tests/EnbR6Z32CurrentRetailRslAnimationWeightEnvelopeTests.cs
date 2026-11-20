using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z32CurrentRetailRslAnimationWeightEnvelopeTests
{
    [Fact]
    public void RenderStateEffectCatalogParsesExactActionEnumAndRetailMilliseconds()
    {
        var catalog = new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("""
            [EFFECT-30000]
            EffectClass=RenderStateEffect
            RenderStateID=1
            ActionType=Linger
            ActionTime=0.2
            ForceReactivate=TRUE

            [EFFECT-30001]
            EffectClass=RenderStateEffect
            RenderStateID=16
            ActionType=rAmP
            ActionTime=0.0019

            [EFFECT-30002]
            EffectClass=RenderStateEffect
            RenderStateID=17
            ActionType=ActivateNextStateOnce
            """);

        EnbEffectDefinition linger = Assert.IsType<EnbEffectDefinition>(catalog.FindEffect(30000));
        Assert.Equal(1, linger.RenderStateId);
        Assert.Equal(EnbRenderStateEffectActionKind.Linger, linger.RenderStateAction);
        Assert.Equal(200, linger.RenderStateActionTimeMilliseconds);
        Assert.True(linger.RenderStateForceReactivate);

        EnbEffectDefinition ramp = Assert.IsType<EnbEffectDefinition>(catalog.FindEffect(30001));
        Assert.Equal(16, ramp.RenderStateId);
        Assert.Equal(EnbRenderStateEffectActionKind.Ramp, ramp.RenderStateAction);
        Assert.Equal(1, ramp.RenderStateActionTimeMilliseconds); // x87 __ftol truncates toward zero.
        Assert.False(ramp.RenderStateForceReactivate);

        EnbEffectDefinition nextOnce = Assert.IsType<EnbEffectDefinition>(catalog.FindEffect(30002));
        Assert.Equal(EnbRenderStateEffectActionKind.ActivateNextStateOnce, nextOnce.RenderStateAction);
        Assert.Equal(1000, nextOnce.RenderStateActionTimeMilliseconds); // current parser default ActionTime=1.0.
    }

    [Fact]
    public void RenderStateEffectMissingFieldsRetainCurrentClientDefaultsInsteadOfInventingSemantics()
    {
        var catalog = new EnbRetailPresentationCatalog();
        catalog.LoadEffectsText("""
            [EFFECT-1]
            EffectClass=RenderStateEffect

            [EFFECT-2]
            EffectClass=RenderStateEffect
            ActionType=SomethingNew
            """);

        EnbEffectDefinition missing = catalog.FindEffect(1)!;
        Assert.Equal(-1, missing.RenderStateId);
        Assert.Equal(EnbRenderStateEffectActionKind.Unknown, missing.RenderStateAction);
        Assert.Equal(1000, missing.RenderStateActionTimeMilliseconds);
        Assert.False(missing.RenderStateForceReactivate);

        Assert.Equal(EnbRenderStateEffectActionKind.Unknown, catalog.FindEffect(2)!.RenderStateAction);
    }

    [Fact]
    public void FreshAnimationActivationUsesUnsettledSentinelWhichContributesFullWeight()
    {
        EnbCurrentRetailRslAnimationWeightEnvelope fresh =
            EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.FreshActivation;

        EnbCurrentRetailRslAnimationWeightUpdate update =
            EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.Update(fresh, 1500, 1000);

        Assert.Equal(-1f, fresh.Value);
        Assert.Equal(1f, update.ContributionWeight);
        Assert.Equal(fresh, update.EnvelopeAfterUpdate);
        Assert.False(update.DeactivatedBeforeAggregateGather);
    }

    [Fact]
    public void LingerStartsAtOneAndAdvancesAfterPublishingCurrentContribution()
    {
        EnbCurrentRetailRslAnimationWeightEnvelope linger =
            EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.BeginLinger(
                EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.FreshActivation,
                200f);

        Assert.Equal(1f, linger.Value);
        Assert.Equal(-0.005f, linger.SlopePerMillisecond, 6);

        EnbCurrentRetailRslAnimationWeightUpdate first =
            EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.Update(linger, 1050, 1000);
        Assert.Equal(1f, first.ContributionWeight);
        Assert.Equal(0.75f, first.EnvelopeAfterUpdate.Value, 6);
        Assert.False(first.DeactivatedBeforeAggregateGather);

        EnbCurrentRetailRslAnimationWeightUpdate second =
            EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.Update(first.EnvelopeAfterUpdate, 1100, 1050);
        Assert.Equal(0.75f, second.ContributionWeight, 6);
        Assert.Equal(0.5f, second.EnvelopeAfterUpdate.Value, 6);
    }

    [Fact]
    public void LingerCrossingBelowZeroDeactivatesBeforeTheAggregateGather()
    {
        var current = new EnbCurrentRetailRslAnimationWeightEnvelope(0.25f, -0.005f);

        EnbCurrentRetailRslAnimationWeightUpdate update =
            EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.Update(current, 1100, 1000);

        Assert.Equal(0.25f, update.ContributionWeight);
        Assert.True(update.DeactivatedBeforeAggregateGather);
        Assert.Equal(new EnbCurrentRetailRslAnimationWeightEnvelope(-1f, current.SlopePerMillisecond), update.EnvelopeAfterUpdate);
    }

    [Fact]
    public void RampStartsAtZeroPreservesMidEnvelopeValueAndSettlesAfterOvershoot()
    {
        EnbCurrentRetailRslAnimationWeightEnvelope ramp =
            EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.BeginRamp(
                EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.FreshActivation,
                200f);
        Assert.Equal(0f, ramp.Value);
        Assert.Equal(0.005f, ramp.SlopePerMillisecond, 6);

        EnbCurrentRetailRslAnimationWeightUpdate first =
            EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.Update(ramp, 1050, 1000);
        Assert.Equal(0f, first.ContributionWeight);
        Assert.Equal(0.25f, first.EnvelopeAfterUpdate.Value, 6);

        EnbCurrentRetailRslAnimationWeightEnvelope reversed =
            EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.BeginLinger(first.EnvelopeAfterUpdate, 100f);
        Assert.Equal(0.25f, reversed.Value, 6); // current retail only seeds when the scalar is outside [0,1].
        Assert.Equal(-0.01f, reversed.SlopePerMillisecond, 6);

        var nearComplete = new EnbCurrentRetailRslAnimationWeightEnvelope(0.9f, 0.005f);
        EnbCurrentRetailRslAnimationWeightUpdate overshoot =
            EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.Update(nearComplete, 1050, 1000);
        Assert.True(overshoot.EnvelopeAfterUpdate.Value > 1f);
        Assert.False(overshoot.DeactivatedBeforeAggregateGather);

        EnbCurrentRetailRslAnimationWeightUpdate settled =
            EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.Update(overshoot.EnvelopeAfterUpdate, 1100, 1050);
        Assert.Equal(1f, settled.ContributionWeight);
        Assert.Equal(overshoot.EnvelopeAfterUpdate, settled.EnvelopeAfterUpdate);
    }


    [Fact]
    public void EnvelopeTickDeltaUsesRetailUnsignedDwordWraparound()
    {
        var current = new EnbCurrentRetailRslAnimationWeightEnvelope(0.5f, 0.001f);

        // Retail computes EAX=current-previous, zero-extends that DWORD into a
        // qword, then FILDs it. 0xFFFFFFF0 -> 0x00000010 is therefore 32 ms.
        EnbCurrentRetailRslAnimationWeightUpdate update =
            EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.Update(current, 0x00000010u, 0xFFFFFFF0u);

        Assert.Equal(0.5f, update.ContributionWeight);
        Assert.Equal(0.532f, update.EnvelopeAfterUpdate.Value, 6);
        Assert.False(update.DeactivatedBeforeAggregateGather);
    }

    [Fact]
    public void SubEpsilonActionTimeUsesRetailDefaultUnitSlope()
    {
        EnbCurrentRetailRslAnimationWeightEnvelope linger =
            EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.BeginLinger(
                EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.FreshActivation,
                0f);
        EnbCurrentRetailRslAnimationWeightEnvelope ramp =
            EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.BeginRamp(
                EnbCurrentRetailRslAnimationWeightEnvelopeRuntime.FreshActivation,
                0f);

        Assert.Equal(-1f, linger.SlopePerMillisecond);
        Assert.Equal(1f, ramp.SlopePerMillisecond);
    }
}
