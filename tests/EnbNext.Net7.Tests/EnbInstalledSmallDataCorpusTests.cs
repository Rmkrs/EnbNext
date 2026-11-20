using System.Text;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // parses installed subtitle, music, chat layout and scene fog data through production loaders
public sealed class EnbInstalledSmallDataCorpusTests
{
    [Fact]
    public void InstalledSubtitlesMusicChatLayoutAndSceneFogParse()
    {
        var assets = EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var lines = new List<string>();
        Assert.True(assets.FileResources.TryRead("subtitle.txt", out var subtitle, out _, out _));
        string text = Encoding.Unicode.GetString(subtitle.AsSpan(2));
        int blocks = 0, recordLines = 0, parsed = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim('\r', ' ', '\t', '\0');
            if (line.StartsWith("BeginMovie", StringComparison.OrdinalIgnoreCase) && line.IndexOf(' ') is int space and > 0)
            {
                blocks++;
                parsed += EnbRetailVideoSubtitles.Parse(subtitle, line[space..].Trim() + ".bik").Count;
            }
            else if (line.StartsWith('<') && line.EndsWith('>')) recordLines++;
        }
        lines.Add($"subtitle blocks {blocks} record lines {recordLines} parsed records {parsed}");
        var music = EnbRetailMusicCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory, "music.ini"));
        lines.Add($"music themes {music.Themes.Count}");
        for (int mode = 0; mode < 3; mode++) { EnbRetailChatLayout.Load(assets, mode, 10); lines.Add($"chat layout {mode} ok"); }
        EnbRetailSceneFogData.Load(assets.Paths);
        lines.Add("scene fog ok");
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "enbnext-small-data-corpus.txt"), lines);
        Assert.Equal(recordLines, parsed);
    }
}
