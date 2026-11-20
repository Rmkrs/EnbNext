using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z25CurrentRetailRslSoundInstanceContractTests
{
    [Fact]
    public void OmittedSoundKeysUseCurrentRetailBinaryFallbacksWithoutOverridingAuthoredValues()
    {
        EnbRetailSoundCatalog catalog = EnbRetailSoundCatalog.ParseText(
            """
            [Bare]
            SoundFilename=bare.wav
            SoundVolume=0.25
            """);

        Assert.True(catalog.TryGetDefinition("Bare", out EnbRetailSoundDefinition sound));
        Assert.Equal(1, sound.EffectiveLoopCount);
        Assert.Equal(1200f, sound.EffectiveDropOffRadius);
        Assert.Equal(400f, sound.EffectiveMaxVolumeRadius);
        Assert.Equal(0.9f, sound.EffectivePriority);
        Assert.Equal(0.25f, sound.EffectiveVolume);
        Assert.Equal(0f, sound.EffectiveMinVolumeShift);
        Assert.Equal(0f, sound.EffectiveMaxVolumeShift);
        Assert.Equal(0f, sound.EffectiveMinPitchShift);
        Assert.Equal(0f, sound.EffectiveMaxPitchShift);
        Assert.Equal(1f, sound.EffectiveMinDopplerShift);
        Assert.Equal(1f, sound.EffectiveMaxDopplerShift);
        Assert.Equal(0.1f, sound.EffectiveDopplerTweak);
        Assert.Equal(1, sound.EffectiveSoundType);
        Assert.True(sound.UsesRetailFallbackForSoundType);
        Assert.Equal(4, sound.EffectiveMaxInstances);
        Assert.Equal(1f, sound.EffectivePitch);
        Assert.Equal(0f, sound.EffectiveSecondsBetweenCalls);
    }

    [Fact]
    public void SoundsIniAndSoundDefaultsRemainAuthoritativeOverRetailFallbacks()
    {
        EnbRetailSoundCatalog catalog = EnbRetailSoundCatalog.ParseText(
            """
            [SOUND_DEFAULTS]
            Default_DropOffRadius=2222
            Default_MaxVolRadius=333
            Default_Priority=0.75
            Default_Volume=0.6
            Default_MaxInstances=7

            [Authored]
            SoundFilename=authored.wav
            SoundLoopCount=8
            SoundType=2
            SoundPitch=1.25
            """);

        Assert.True(catalog.TryGetDefinition("Authored", out EnbRetailSoundDefinition sound));
        Assert.Equal(8, sound.EffectiveLoopCount);
        Assert.Equal(2222f, sound.EffectiveDropOffRadius);
        Assert.Equal(333f, sound.EffectiveMaxVolumeRadius);
        Assert.Equal(0.75f, sound.EffectivePriority);
        Assert.Equal(0.6f, sound.EffectiveVolume);
        Assert.Equal(2, sound.EffectiveSoundType);
        Assert.False(sound.UsesRetailFallbackForSoundType);
        Assert.Equal(7, sound.EffectiveMaxInstances);
        Assert.Equal(1.25f, sound.EffectivePitch);
    }

    [Fact]
    public void RetailSoundPrototypeBitIsRemovedByTheCurrentCloneMaskBeforeHandleResolution()
    {
        EnbCurrentRetailRslSoundContract contract = EnbCurrentRetailRslSoundRuntime.Describe(
            Flags(dontStopSound: true));

        Assert.Equal(0x0002_0000u, contract.ParsedFlags);
        Assert.Equal(0x0002_8000u, contract.PrototypeFlags);
        Assert.Equal(0x0002_0000u, contract.InstanceFlags);
        Assert.True(contract.PrototypeHandleBlocked);
        Assert.True(contract.InstanceHandleEligible);
        Assert.False(contract.StopsHandleOnCleanup);
    }

    [Fact]
    public void RetailCloneMaskPreservesOnlyTheObservedCopyableStateBits()
    {
        EnbCurrentRetailRslSoundContract contract = EnbCurrentRetailRslSoundRuntime.Describe(
            Flags(
                delete: true,
                deactivate: true,
                stop: true,
                activateNext: true,
                enabled: true,
                activateNextOnce: true,
                precached: true,
                instanceData: true,
                removePrototype: true,
                stationary: true,
                startsEnabled: true,
                dontStopSound: true,
                dontLoopSound: true));

        Assert.Equal(EnbCurrentRetailRslSoundRuntime.ClonePreservedFlagMask, contract.InstanceFlags);
        Assert.Equal(0u, contract.InstanceFlags & EnbCurrentRetailRslSoundRuntime.PrototypeOnlyFlag);
    }

    [Fact]
    public void RetailRslSoundOverridesHandleLoopControlFromActivateNextAndDontLoopBranches()
    {
        Assert.Equal(0, EnbCurrentRetailRslSoundRuntime.Describe(Flags()).HandleLoopCountOverride);
        Assert.Equal(1, EnbCurrentRetailRslSoundRuntime.Describe(Flags(activateNext: true)).HandleLoopCountOverride);
        Assert.Equal(1, EnbCurrentRetailRslSoundRuntime.Describe(Flags(dontLoopSound: true)).HandleLoopCountOverride);
    }

    private static EnbRslStateFlags Flags(
        bool delete = false,
        bool deactivate = false,
        bool stop = false,
        bool activateNext = false,
        bool enabled = false,
        bool activateNextOnce = false,
        bool precached = false,
        bool instanceData = false,
        bool removePrototype = false,
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
            StopOnce: false,
            Stationary: stationary,
            StartsEnabled: startsEnabled,
            DontStopSound: dontStopSound,
            DontLoopSound: dontLoopSound);
}
