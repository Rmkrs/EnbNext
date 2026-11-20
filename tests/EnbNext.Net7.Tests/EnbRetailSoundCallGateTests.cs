using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailSoundCallGateTests
{
    private static EnbRetailSoundDefinition Definition => EnbRetailSoundCatalog.ParseText(
        "[On_Destroy]\nSoundFilename=voice.mp3\nSoundType=2\nSoundSecsBetweenCalls=4").GetDefinitionOrFile("On_Destroy");

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue - 1000)]
    public void RepeatedCallsAreDroppedWithoutExtendingDeadline(uint start)
    {
        var gate=new EnbRetailSoundCallGate();
        Assert.True(gate.TryCall(Definition,start));
        Assert.False(gate.TryCall(Definition with {Name="on_destroy"},unchecked(start+3999)));
        Assert.True(gate.TryCall(Definition,unchecked(start+4000)));
        Assert.False(gate.TryCall(Definition,unchecked(start+4001)));
        Assert.True(gate.TryCall(Definition with {Name="another_voice"},unchecked(start+4001)));
    }

    [Fact]
    public void RateLimitedSpeechRequiresPlaybackOwnerSupport()
    {
        Assert.NotNull(EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(Definition,allowSpeech:true));
        Assert.Null(EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(Definition,allowSpeech:true,allowRateLimiting:true));
        Assert.Null(EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(Definition,allowRateLimiting:true));
        Assert.NotNull(EnbCurrentRetailEffectSoundRuntime.ValidateDefinition(Definition,allowSpeech:false,allowRateLimiting:true));
        var gate=new EnbRetailSoundCallGate();
        Assert.True(gate.TryCall(Definition with {SecondsBetweenCalls=0},0));
        Assert.True(gate.TryCall(Definition with {SecondsBetweenCalls=0},0));
        Assert.True(gate.TryCall(Definition with {SecondsBetweenCalls=float.NaN},0));
        Assert.True(gate.TryCall(Definition with {SecondsBetweenCalls=float.NaN},0));
    }

    [Theory]
    [InlineData(4f, 4000)]
    [InlineData(0.0019f, 1)]
    [InlineData(-2.5f, -2500)]
    [InlineData(3e6f, -1294967296)]
    [InlineData(float.NaN, 0)]
    [InlineData(float.PositiveInfinity, 0)]
    [InlineData(1e30f, 0)]
    public void IntervalUsesRetailFtolLowDword(float seconds, int milliseconds) =>
        Assert.Equal(milliseconds, EnbRetailSoundCallGate.RetailIntervalMilliseconds(seconds));

    [Fact]
    public void NegativeIntervalAdmitsEveryLaterCallAsRetailDeadlineDoes()
    {
        var gate=new EnbRetailSoundCallGate();var negative=Definition with {SecondsBetweenCalls=-2};
        Assert.True(gate.TryCall(negative,10_000));
        Assert.True(gate.TryCall(negative,10_000));
        Assert.True(gate.TryCall(negative,10_001));
    }
}
