using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbSharedOptionsTests
{
    private const string Source = "[BOOL-000]\r\nOptionName=Camera_Warp\r\nOptionValue=no\r\n\r\n[BOOL-001]\r\nOptionName=Channel_Enforcers\r\nOptionValue=yes\r\n\r\n" +
        "[FLOAT-000]\r\nOptionName=Graphics_Gamma\r\nOptionValue=80\r\n\r\n[FLOAT-001]\r\nOptionName=Sound_Music\r\nOptionValue=10\r\n\r\n" +
        "[INT-000]\r\nOptionName=Current_Log_Page\r\nOptionValue=7\r\n\r\n[INT-001]\r\nOptionName=Interface_Tooltip_Delay\r\nOptionValue=3\r\n";

    private static Dictionary<string, string> Values(string text) => EnbRetailIniDocument.Parse(text).EnumerateSections()
        .Where(s => s.Value.ContainsKey("OptionName")).ToDictionary(s => s.Value["OptionName"], s => s.Value.GetValueOrDefault("OptionValue", ""));

    [Fact]
    public void SharedOptionsReplaceTheCharactersOwnButGraphicsSoundChannelsAndLogPageStay()
    {
        const string own = "[BOOL-000]\r\nOptionName=Camera_Warp\r\nOptionValue=yes\r\n\r\n[BOOL-001]\r\nOptionName=Channel_Sentinels\r\nOptionValue=yes\r\n\r\n" +
            "[FLOAT-000]\r\nOptionName=Graphics_Gamma\r\nOptionValue=20\r\n\r\n[FLOAT-001]\r\nOptionName=Sound_Music\r\nOptionValue=55\r\n\r\n" +
            "[INT-000]\r\nOptionName=Current_Log_Page\r\nOptionValue=2\r\n";
        var merged = Values(EnbRetailCinematicOptionsWriter.ShareOptions(Source, own));
        Assert.Equal("no", merged["Camera_Warp"]);
        Assert.Equal("3", merged["Interface_Tooltip_Delay"]);
        Assert.Equal("20", merged["Graphics_Gamma"]);
        Assert.Equal("55", merged["Sound_Music"]);
        Assert.Equal("2", merged["Current_Log_Page"]);
        Assert.Equal("yes", merged["Channel_Sentinels"]);
        Assert.False(merged.ContainsKey("Channel_Enforcers"));
    }

    [Fact]
    public void ANewCharacterGetsTheSharedOptionsOnly()
    {
        var merged = Values(EnbRetailCinematicOptionsWriter.ShareOptions(Source, ""));
        Assert.Equal("no", merged["Camera_Warp"]);
        Assert.Equal("3", merged["Interface_Tooltip_Delay"]);
        Assert.False(merged.ContainsKey("Graphics_Gamma"));
        Assert.False(merged.ContainsKey("Sound_Music"));
        Assert.False(merged.ContainsKey("Current_Log_Page"));
    }
}
