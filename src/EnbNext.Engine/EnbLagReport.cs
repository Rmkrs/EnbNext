using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EnbNext.Engine;

/// <summary>
/// Next: a lag report package for the developer, as the Forge accepts it: a zip of <c>report.json</c> (the summary)
/// and plain text files. Names the player would not want shared (the account, the characters, the computer's user and
/// name) are taken out of every file first.
/// </summary>
internal static class EnbLagReport
{
    public const string UploadAddress = "https://net7forge.com/api/v1/enbnext/reports";
    /// <summary>The Forge's limit for one package.</summary>
    public const int MaximumPackageBytes = 5 * 1024 * 1024;
    private const string Removed = "<name>";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The package: the summary and the files, each scrubbed of the names.</summary>
    public static byte[] Package(JsonObject summary, IReadOnlyList<(string Name, string Text)> files, IEnumerable<string?> names)
    {
        var scrub = Names(names);
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "report.json", Scrub(summary.ToJsonString(Indented), scrub));
            foreach (var (name, text) in files) Add(archive, name, Scrub(text, scrub));
        }
        // A long session's transcript is cut from its start until the package fits.
        if (memory.Length > MaximumPackageBytes && files.Any(f => f.Text.Length > 4096))
        {
            var largest = files.MaxBy(f => f.Text.Length);
            var shorter = files.Select(f => f == largest ? (f.Name, f.Text[(f.Text.Length / 2)..]) : f).ToList();
            return Package(summary, shorter, scrub);
        }
        return memory.ToArray();
    }

    private static void Add(ZipArchive archive, string name, string text)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>The names worth removing: at least three characters, longest first (a full name before its first name).</summary>
    private static List<string> Names(IEnumerable<string?> names)
    {
        var list = new List<string>();
        foreach (string? name in names)
        {
            string? trimmed = name?.Trim();
            if (trimmed is { Length: >= 3 } && !list.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) list.Add(trimmed);
        }
        list.Sort((a, b) => b.Length.CompareTo(a.Length));
        return list;
    }

    /// <summary>The text with every name replaced, whatever its case.</summary>
    public static string Scrub(string text, IEnumerable<string?> names)
    {
        foreach (string name in Names(names))
            text = text.Replace(name, Removed, StringComparison.OrdinalIgnoreCase);
        return text;
    }

    /// <summary>The report number from the Forge's answer, or null when the answer has none.</summary>
    public static long? ReportNumber(string answer)
    {
        try
        {
            using var document = JsonDocument.Parse(answer);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("report", out var report) &&
                report.TryGetInt64(out long number) ? number : null;
        }
        catch (JsonException) { return null; }
    }
}
