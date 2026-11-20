using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbMessageFontEffectsTests
{
    private const string FontArt = """
        [EFFECT-1]
        EffectID=on
        EffectType=FADE
        Initial=0
        Intensity=1
        Speed=3
        [EFFECT-2]
        EffectID=complete
        EffectType=STAY
        Time=-1
        [EFFECT-3]
        EffectID=hold
        EffectType=STAY
        Time=3
        [EFFECT-4]
        EffectID=off
        EffectType=FADE
        Initial=1
        Intensity=0
        Speed=1
        """;

    [Fact]
    public void AuthoredFadeUsesRetailHalfMillisecondRateAndWaitsForOffCompletion()
    {
        var layout = EnbInterfaceLayoutCatalog.ParseText("""
            [STR-1]
            StringID=Simple
            EffectOn=on,complete
            EffectConst=hold
            EffectOff=off
            """);
        Assert.True(layout.TryResolve("Simple", out var style));
        Assert.True(EnbMessageFontEffects.TryCreate(style, EnbRetailIniDocument.Parse(FontArt), out var effects));
        effects.Advance(0); Assert.Equal(0, effects.Opacity);
        effects.Advance(400); Assert.Equal(0, effects.Opacity);
        effects.Advance(300); Assert.Equal(.6f, effects.Opacity, 5);
        effects.Advance(0); Assert.Equal(1, effects.Opacity); Assert.False(effects.Complete);
        effects.Advance(2999); Assert.False(effects.Complete);
        effects.Advance(1); Assert.Equal(1, effects.Opacity);
        effects.Advance(1000); Assert.Equal(1, effects.Opacity);
        effects.Advance(1000); Assert.Equal(.5f, effects.Opacity, 5); Assert.False(effects.Complete);
        effects.Advance(0); Assert.Equal(0, effects.Opacity); Assert.True(effects.Complete);
    }

    [Fact]
    public void RotateAndBlinkUseRetailAnglesAndHalfSecondPeriod()
    {
        var layout = EnbInterfaceLayoutCatalog.ParseText("[STR-1]\nStringID=Fancy\nEffectOn=rotate,complete\nEffectConst=blink,hold\nEffectOff=off");
        layout.TryResolve("Fancy", out var style);
        var fontArt = EnbRetailIniDocument.Parse(FontArt + "\n[EFFECT-5]\nEffectID=rotate\nEffectType=ROTATE\nInitial=0\nIntensity=1\nSpeed=.25\n[EFFECT-6]\nEffectID=blink\nEffectType=BLINK\nSpeed=1\n");
        Assert.True(EnbMessageFontEffects.TryCreate(style, fontArt, out var effects));
        effects.Advance(0); Assert.Equal(0, effects.VerticalScale);
        effects.Advance(500); effects.Advance(0);
        Assert.InRange(effects.VerticalScale, .70709f, .70712f);
        effects.Advance(501); effects.Advance(0); Assert.Equal(1, effects.VerticalScale, 5);
        effects.Advance(125); Assert.Equal(.5f, effects.Opacity, 5);
        effects.Advance(125); Assert.Equal(1, effects.Opacity);
        effects.Advance(125); Assert.Equal(.5f, effects.Opacity, 5);
        effects.Advance(125); Assert.Equal(0, effects.Opacity);
        effects.Advance(2500); effects.Advance(2000); effects.Advance(0);
        Assert.True(effects.Complete);
    }

    [Fact]
    public void RandomRevealWaitsPastLastCharacterThenUsesDefaultContinuousRotation()
    {
        var layout = EnbInterfaceLayoutCatalog.ParseText("[STR-1]\nStringID=Complex\nEffectOn=reveal,complete\nEffectConst=rotate,hold\nEffectOff=off");
        layout.TryResolve("Complex", out var style);
        var fontArt = EnbRetailIniDocument.Parse(FontArt + "\n[EFFECT-5]\nEffectID=reveal\nEffectType=RANDLETTER\nInitial=0\nIntensity=1\nSpeed=2\nDirection=RANDOM\n[EFFECT-6]\nEffectID=rotate\nEffectType=ROTATE\nSpeed=1\n");
        Assert.True(EnbMessageFontEffects.TryCreate(style, fontArt, out var effects, "ABCD", () => 0));
        effects.Advance(0); Assert.Equal("    ", effects.DisplayText);
        effects.Advance(100); Assert.Equal("A   ", effects.DisplayText);
        effects.Advance(300); Assert.Equal("ABCD", effects.DisplayText); Assert.False(effects.Complete);
        effects.Advance(100); Assert.Null(effects.DisplayText);
        effects.Advance(250); Assert.Equal(1, effects.VerticalScale);
        effects.Advance(250); Assert.InRange(effects.VerticalScale, -.0001f, .0001f);
        effects.Advance(250); Assert.Equal(-1, effects.VerticalScale, 5);
        effects.Advance(2250); Assert.Equal(1, effects.VerticalScale);
        effects.Advance(2000); effects.Advance(0); Assert.True(effects.Complete);
    }

    [Fact]
    public void AbsentEffectNamesAreNativeOmissions()
    {
        var layout = EnbInterfaceLayoutCatalog.ParseText("[STR-1]\nStringID=plain\n[STR-2]\nStringID=missing\nEffectOn=rotate");
        layout.TryResolve("plain", out var plain); layout.TryResolve("missing", out var missing);
        var fontArt = EnbRetailIniDocument.Parse(FontArt);
        Assert.True(EnbMessageFontEffects.TryCreate(plain, fontArt, out var effects));
        effects.Advance(0); Assert.True(effects.Complete); Assert.Equal(1, effects.Opacity);
        Assert.True(EnbMessageFontEffects.TryCreate(missing, fontArt, out var omitted));
        omitted.Advance(0); Assert.True(omitted.Complete); Assert.Equal(1, omitted.Opacity);
    }
    [Fact]
    public void AuthoredNonePhasesDoNotSuppressConstantBlink()
    {
        var layout=EnbInterfaceLayoutCatalog.ParseText("[STR-1]\nStringID=message\nEffectOn=NONE\nEffectConst=blink\nEffectOff=NONE");
        layout.TryResolve("message",out var style);
        Assert.True(EnbMessageFontEffects.TryCreate(style,EnbRetailIniDocument.Parse("[EFFECT-1]\nEffectID=blink\nEffectType=BLINK\nSpeed=1"),out var effect));
        effect.Advance(0);Assert.Equal(0,effect.Opacity);
        effect.Advance(250);Assert.Equal(1,effect.Opacity);
        effect.Advance(250);Assert.Equal(0,effect.Opacity);Assert.False(effect.Complete);
    }

}
