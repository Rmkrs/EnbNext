using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10EngineDopplerTests
{
    private static EnbRetailSoundDefinition Definition => EnbRetailSoundCatalog.ParseText(
        "[engine]\nSoundFilename=a.wav\nSoundLoopCount=0\nSoundMinDopplerShift=.9\nSoundMaxDopplerShift=1.1\nSoundDopplerTweak=1").GetDefinitionOrFile("engine");

    [Fact]
    public void FirstSampleSeedsAndApproachRecedeAreBounded()
    {
        var voice = new EnbCurrentRetailEngineDopplerRuntime(Definition);
        Assert.Equal(1, voice.Update(new(0, 0, 10000), Vector3.Zero, 100, true));
        Assert.Equal(1.1f, voice.Update(new(0, 0, 9000), Vector3.Zero, 1100, true));
        Assert.Equal(.9f, voice.Update(new(0, 0, 10000), Vector3.Zero, 2100, true));
    }

    [Fact]
    public void CoMovingListenerAndDuplicateSamplesDoNotInventMotion()
    {
        var voice = new EnbCurrentRetailEngineDopplerRuntime(Definition);
        voice.Update(new(0, 0, 100), Vector3.Zero, 0, true);
        Assert.Equal(1, voice.Update(new(0, 0, 116), new(0, 0, 16), 16, true));
        Assert.Equal(1, voice.Update(new(0, 0, 1000), Vector3.Zero, 16, true));
        Assert.Equal(1, voice.Update(new(0, 0, 132), new(0, 0, 32), 32, true));
    }

    [Fact]
    public void DisabledOptionRestoresBaseAndReseedsHistory()
    {
        var voice = new EnbCurrentRetailEngineDopplerRuntime(Definition);
        voice.Update(new(0, 0, 1000), Vector3.Zero, 0, true);
        Assert.True(voice.Update(new(0, 0, 900), Vector3.Zero, 100, true) > 1);
        Assert.Equal(1, voice.Update(new(0, 0, 800), Vector3.Zero, 200, false));
        Assert.Equal(1, voice.Update(new(0, 0, 700), Vector3.Zero, 300, false));
        Assert.Equal(1, voice.Update(new(0, 0, 600), new(0, 0, -100), 400, true));
        var fixedPitch = new EnbCurrentRetailEngineDopplerRuntime(Definition with { Pitch = .8f, MinDopplerShift = 1, MaxDopplerShift = 1 });
        fixedPitch.Update(new(0, 0, 1000), Vector3.Zero, 0, true);
        Assert.Equal(.8f, fixedPitch.Update(new(0, 0, 500), Vector3.Zero, 1000, true));
    }

    [Fact]
    public void AbsoluteBoundsUseStartingPitchNotVolume()
    {
        var voice = new EnbCurrentRetailEngineDopplerRuntime(Definition with { Pitch = 2, Volume = .1f });
        Assert.Equal(2, voice.Update(new(0, 0, 10000), Vector3.Zero, 0, true));
        Assert.Equal(1.8f, voice.Update(new(0, 0, 11000), Vector3.Zero, 1000, true));
    }

    [Fact]
    public void ClockWrapWorksAndBackwardClockOrInvalidTransformReseeds()
    {
        var voice = new EnbCurrentRetailEngineDopplerRuntime(Definition);
        voice.Update(new(0, 0, 1000), Vector3.Zero, uint.MaxValue - 15, true);
        Assert.True(voice.Update(new(0, 0, 984), Vector3.Zero, 0, true) > 1);
        Assert.Equal(1, voice.Update(new(0, 0, 900), Vector3.Zero, uint.MaxValue - 10, true));
        Assert.Equal(1, voice.Update(new(float.NaN, 0, 0), Vector3.Zero, 20, true));
        Assert.Equal(1, voice.Update(new(0, 0, 100000), Vector3.Zero, 30, true));
    }

    [Fact]
    public void StationarySourceKeepsRetailUpdateGate()
    {
        var voice = new EnbCurrentRetailEngineDopplerRuntime(Definition);
        voice.Update(new(0, 0, 1000), Vector3.Zero, 0, true);
        Assert.Equal(1, voice.Update(new(0, 0, 1000), new(0, 0, 900), 1000, true));
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("no", false)]
    public void PersistedRetailOptionOverridesEnabledDefault(string value, bool enabled)
    {
        Assert.True(EnbRetailPlayerOptionsCatalog.ParseText("").DopplerEnabled);
        Assert.Equal(enabled, EnbRetailPlayerOptionsCatalog.ParseText($"[BOOL-1]\nOptionName=Doppler_Enabled\nOptionValue={value}").DopplerEnabled);
    }

    [Fact]
    public void InvalidAuthoredLimitsCannotReachPlayback()
    {
        Assert.NotNull(EnbCurrentRetailEngineSoundRuntime.ValidateDefinition(Definition with { MinDopplerShift = 0 }));
        Assert.NotNull(EnbCurrentRetailEngineSoundRuntime.ValidateDefinition(Definition with { MinDopplerShift = 2 }));
        Assert.NotNull(EnbCurrentRetailEngineSoundRuntime.ValidateDefinition(Definition with { DopplerTweak = float.NaN }));
    }
}
