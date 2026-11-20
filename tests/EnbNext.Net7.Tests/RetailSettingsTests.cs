using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class RetailSettingsTests
{
    private const string FontIni = "[INIT-0]\nResWidth=640\nResHeight=480\nFont4=font_a.tga\nFontID4=FONT_CHAT_LINE_00\nScale4=1\nSpacing4=-1\n[INIT-1]\nTemplate0=INIT-0\nResWidth=1280\nResHeight=720\n[ALL_FONTS]\n0=font_a.tga\n1=font_b.tga\n";

    [Fact]
    public void FontPersistenceRoundTripsMetricsAndPreservesOtherResolutionAndOptions()
    {
        var catalog = EnbFontSystemCatalog.ParseText(FontIni);
        var profile = catalog.Profiles[1];
        var font = profile.Fonts.Single() with { Resource = "font_b.tga", Scale = 1.4f, Spacing = -3, LineSpacing = 5 };
        var previous = "640x480:font_a.tga:0.7,-1,2\\n1280x720:font_a.tga:1.0,0,0\\n";
        var value = EnbRetailChatFonts.Update(previous, profile, font);
        var text = EnbRetailCinematicOptionsWriter.UpdateValues("[BOOL-000]\nOptionName=Bridge_Mode\nOptionValue=yes\n",
            [("STRING", "FONT_CHAT_LINE_00", value)]);
        var options = EnbRetailPlayerOptionsCatalog.ParseText(text);
        Assert.True(options.BridgeMode);
        Assert.Equal(font, EnbRetailChatFonts.Resolve(profile, profile.Fonts.Single(), options.FontOptions, catalog.AvailableFonts));
        var small = EnbRetailChatFonts.Resolve(catalog.Profiles[0], catalog.Profiles[0].Fonts.Single(), options.FontOptions, catalog.AvailableFonts);
        Assert.Equal(.7f, small.Scale);
        Assert.Equal(2, small.LineSpacing);
        Assert.Single(value.Split("\\n"), s => s.StartsWith("1280x720:"));
    }

    [Theory]
    [InlineData("640x480:missing.tga:1.0,0,0\\n")]
    [InlineData("640x480:font_a.tga:NaN,0,0\\n")]
    [InlineData("640x480:font_a.tga:1.0,broken,0\\n")]
    public void BadPersistedFontKeepsAuthoredDefinition(string value)
    {
        var catalog = EnbFontSystemCatalog.ParseText(FontIni);
        var profile = catalog.Profiles[0]; var authored = profile.Fonts.Single();
        Assert.Equal(authored, EnbRetailChatFonts.Resolve(profile, authored,
            new Dictionary<string,string> { [authored.FontId!] = value }, catalog.AvailableFonts));
    }

    [Fact]
    public void SoundAndFontPreviewsAreSessionLocalAndPersistenceSurvivesReload()
    {
        string root = Path.Combine(Path.GetTempPath(), "enb-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root,"release"));
            string ini = Path.Combine(root,"Data","client","ini"); Directory.CreateDirectory(ini);
            Directory.CreateDirectory(Path.Combine(root,"Data","client","mixfiles"));
            File.WriteAllBytes(Path.Combine(root,"release","client.exe"),[0x4d,0x5a]);
            File.WriteAllText(Path.Combine(ini,"basset.ini"),"[BASE-1]\nNULLOBJECT\n");
            File.WriteAllText(Path.Combine(ini,"fontsys.ini"),FontIni);
            var paths = EnbInstalledAssetLocator.TryResolveRoot(root)!;
            var bundle = EnbRetailAssetBundle.Load(paths);
            var initial = bundle.ResolvePlayerOptions(123).Options;
            Assert.Equal(100, initial.SoundDialog);
            bundle.SetSoundPreview(123,new(0,21,42,false));
            Assert.Equal(42,bundle.ResolvePlayerOptions(123).Options.SoundDialog);
            Assert.Equal(initial.SoundDialog,bundle.ResolvePlayerOptions(124).Options.SoundDialog);
            Assert.True(bundle.ResolvePlayerOptions(123).Options.DopplerEnabled); // Checkbox applies only on OK.
            bundle.SetSoundPreview(123,null);
            Assert.Equal(initial,bundle.ResolvePlayerOptions(123).Options);
            bundle.SaveSoundOptions(123,new(0,21,42,false));
            bundle.SaveInterfaceOptions(123,17);
            var profile = bundle.Fonts!.Profiles[0]; var authored = profile.Fonts.Single();
            var changed = authored with { Scale = 1.7f, Spacing = 3, LineSpacing = -2 };
            bundle.SetChatFontPreview(123,profile,[changed]);
            Assert.Equal(changed,bundle.ResolveChatFont(123,profile,authored));
            Assert.Equal(authored,bundle.ResolveChatFont(124,profile,authored));
            bundle.SetChatFontPreview(123,profile,null);
            Assert.Equal(authored,bundle.ResolveChatFont(123,profile,authored));
            bundle.SaveChatFonts(123,profile,[changed]);
            var reloaded = EnbRetailAssetBundle.Load(paths);
            Assert.Equal(changed,reloaded.ResolveChatFont(123,profile,authored));
            var saved = reloaded.ResolvePlayerOptions(123).Options;
            Assert.Equal(0,saved.SoundMusic); Assert.Equal(21,saved.SoundSfx); Assert.Equal(42,saved.SoundDialog);
            Assert.False(saved.DopplerEnabled); Assert.Equal(17,saved.InterfaceTooltipDelay);
        }
        finally { Directory.Delete(root,true); }
    }
}
