using System.Globalization;
using System.Text;

namespace EnbNext.Assets;

/// <summary>Retail 005ABCC0 / 005AC0D0 numbered, character-scoped log storage.</summary>
public sealed class EnbRetailCaptainLog(string outputDirectory, uint sessionId)
{
    private readonly int _selector = EnbRetailPlayerOptionsCatalog.GetCurrentCompatibilitySelector(sessionId);
    private string OptionsPath => Path.Combine(outputDirectory, $"player{_selector}_options.ini");
    private string PagePath(int page)
    {
        if (page is < 1 or > 999) throw new ArgumentOutOfRangeException(nameof(page));
        return Path.Combine(outputDirectory, $"player{_selector}_log-{page}.ini");
    }

    public int ReadCurrentPage()
    {
        if (!EnbNext.Installation.EnbPathCase.FileExists(OptionsPath)) return 1;
        foreach (var (section, values) in EnbRetailIniDocument.Load(OptionsPath).EnumerateSections())
            if (section.StartsWith("INT-", StringComparison.OrdinalIgnoreCase) &&
                values.GetValueOrDefault("OptionName", "").Equals("Current_Log_Page", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(values.GetValueOrDefault("OptionValue"), out int page))
                return Math.Clamp(page, 1, 999);
        return 1;
    }

    public string ReadPage(int page) => ReadPage(page,out _);

    public string ReadPage(int page, out bool initialized)
    {
        initialized = false;
        string path = PagePath(page);
        if (!EnbNext.Installation.EnbPathCase.FileExists(path)) return "";
        var ini = EnbRetailIniDocument.Parse(File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(path),Encoding.Latin1));
        if (!ini.TryGetSection("NUM_LINES", out var count) ||
            !EnbRetailIniDocument.TryReadInt(count, "LINES", out int lines) || lines <= 0) return "";
        var text = new StringBuilder();
        initialized = true;
        // 006729B0 -> 0084B200/0084B020 copies into the 005AB030 0x400-byte editor.
        for (int i = 0; i < lines && text.Length < 1024; i++)
        {
            if (i != 0) text.Append('\n');
            if (ini.TryGetSection($"LINE-{i:0000}", out var line))
            {
                string value = line.GetValueOrDefault("TEXT", "").Replace("/:,", ";", StringComparison.Ordinal);
                text.Append(value.AsSpan(0,Math.Min(value.Length,1024-text.Length)));
            }
        }
        return text.ToString();
    }

    public void SavePage(int page, string text)
    {
        string path = PagePath(page);
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = new StringBuilder().Append("[NUM_LINES]\r\nLINES=").Append(lines.Length).Append("\r\n");
        for (int i = 0; i < lines.Length; i++)
            result.Append("\r\n[LINE-").Append(i.ToString("0000", CultureInfo.InvariantCulture)).Append("]\r\nTEXT=")
                .Append(lines[i].Replace(";", "/:,", StringComparison.Ordinal)).Append("\r\n");
        Write(path, result.ToString(),Encoding.Latin1);
    }

    public void SaveCurrentPage(int page)
    {
        _ = PagePath(page);
        string old = EnbNext.Installation.EnbPathCase.FileExists(OptionsPath) ? File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(OptionsPath)) : "";
        string updated = EnbRetailCinematicOptionsWriter.UpdateValues(old,
            [("INT", "Current_Log_Page", page.ToString(CultureInfo.InvariantCulture))]);
        if (old != updated) Write(OptionsPath, updated);
    }

    private static void Write(string path, string text, Encoding? encoding = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text,encoding ?? new UTF8Encoding(false)); File.Move(temporary, EnbNext.Installation.EnbPathCase.Resolve(path), true); }
        finally { if (EnbNext.Installation.EnbPathCase.FileExists(temporary)) File.Delete(temporary); }
    }
}
