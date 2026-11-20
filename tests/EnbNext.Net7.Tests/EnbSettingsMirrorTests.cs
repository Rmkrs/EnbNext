using System.Text;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

/// <summary>Settings sections for all characters: clients sharing one folder, as running alts do.</summary>
public sealed class EnbSettingsMirrorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "enbnext-mirror-" + Guid.NewGuid().ToString("N"));
    private string Shared => Path.Combine(_folder, "all-characters", "game-options.ini");
    private string Own(string name) => Path.Combine(_folder, name + "_options.ini");

    private static EnbSettingsMirror GameOptions() => new(Encoding.Latin1, (shared, own) => own is null ? shared : EnbRetailCinematicOptionsWriter.ShareOptions(shared, own));
    private static string Options(string warp, string gamma) =>
        "[BOOL-000]\r\nOptionName=Camera_Warp\r\nOptionValue=" + warp + "\r\n\r\n[FLOAT-000]\r\nOptionName=Graphics_Gamma\r\nOptionValue=" + gamma + "\r\n";
    private static Dictionary<string, string> Values(string path) => EnbRetailIniDocument.Parse(File.ReadAllText(path, Encoding.Latin1)).EnumerateSections()
        .Where(s => s.Value.ContainsKey("OptionName")).ToDictionary(s => s.Value["OptionName"], s => s.Value.GetValueOrDefault("OptionValue", ""));

    public EnbSettingsMirrorTests() => Directory.CreateDirectory(_folder);
    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    [Fact]
    public void AChangeOnAnyCharacterReachesTheOthersAndTheyKeepTheirOwnGraphics()
    {
        File.WriteAllText(Own("main"), Options("yes", "20"), Encoding.Latin1);
        File.WriteAllText(Own("alt"), Options("no", "80"), Encoding.Latin1);
        var main = GameOptions(); var alt = GameOptions();

        main.Seed(Own("main"), Shared);
        Assert.Equal(EnbSettingsMirror.Change.Taken, alt.Step(Own("alt"), Shared));
        Assert.Equal("yes", Values(Own("alt"))["Camera_Warp"]);
        Assert.Equal("80", Values(Own("alt"))["Graphics_Gamma"]);

        // Nothing changed: no client writes again (no back and forth).
        for (int round = 0; round < 3; round++)
        {
            Assert.Equal(EnbSettingsMirror.Change.None, main.Step(Own("main"), Shared));
            Assert.Equal(EnbSettingsMirror.Change.None, alt.Step(Own("alt"), Shared));
        }

        // The alt changes an option: it becomes everyone's, the main keeps its own graphics.
        File.WriteAllText(Own("alt"), Options("no", "80"), Encoding.Latin1);
        Assert.Equal(EnbSettingsMirror.Change.Shared, alt.Step(Own("alt"), Shared));
        Assert.Equal(EnbSettingsMirror.Change.Taken, main.Step(Own("main"), Shared));
        Assert.Equal("no", Values(Own("main"))["Camera_Warp"]);
        Assert.Equal("20", Values(Own("main"))["Graphics_Gamma"]);
        for (int round = 0; round < 3; round++)
        {
            Assert.Equal(EnbSettingsMirror.Change.None, alt.Step(Own("alt"), Shared));
            Assert.Equal(EnbSettingsMirror.Change.None, main.Step(Own("main"), Shared));
        }
    }

    [Fact]
    public void ANewCharacterTakesTheSharedSettingsWhenSelected()
    {
        File.WriteAllText(Own("main"), Options("no", "20"), Encoding.Latin1);
        GameOptions().Seed(Own("main"), Shared);
        var fresh = GameOptions();
        Assert.True(fresh.Take(Own("new"), Shared));
        Assert.Equal("no", Values(Own("new"))["Camera_Warp"]);
        Assert.Equal(EnbSettingsMirror.Change.None, fresh.Step(Own("new"), Shared));
    }

    [Fact]
    public void TheFirstCharacterStartsTheSharedSettings()
    {
        File.WriteAllText(Own("main"), Options("yes", "20"), Encoding.Latin1);
        var main = GameOptions();
        Assert.Equal(EnbSettingsMirror.Change.Shared, main.Step(Own("main"), Shared));
        Assert.Equal(File.ReadAllText(Own("main"), Encoding.Latin1), File.ReadAllText(Shared, Encoding.Latin1));
        Assert.Equal(EnbSettingsMirror.Change.None, main.Step(Own("main"), Shared));
    }
}
