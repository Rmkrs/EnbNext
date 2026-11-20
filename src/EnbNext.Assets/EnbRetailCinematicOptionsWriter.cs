using System.Text.RegularExpressions;
namespace EnbNext.Assets;

public static class EnbRetailCinematicOptionsWriter
{

    internal static string UpdateBooleans(string text, (string Name, bool Enabled)[] values)
        => UpdateValues(text, values.Select(v => ("BOOL", v.Name, v.Enabled ? "yes" : "no")).ToArray());

    internal static string UpdateFloats(string text, (string Name, float Value)[] values)
        => UpdateValues(text, values.Select(v => ("FLOAT", v.Name,
            Math.Clamp(v.Value, 0f, 100f).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture))).ToArray());

    /// <summary>
    /// Next "Same settings for all characters": another character's options file with this character's own options
    /// kept (graphics, sound, its chat channels and its captain's log page). The other character's remaining options
    /// replace or join this character's; this character's other options stay.
    /// </summary>
    public static string ShareOptions(string source, string own)
    {
        var values = new List<(string Kind, string Name, string Value)>();
        foreach (var (section, entries) in EnbRetailIniDocument.Parse(source).EnumerateSections())
        {
            int dash = section.IndexOf('-');
            if (dash <= 0 || !entries.TryGetValue("OptionName", out var name) || !entries.TryGetValue("OptionValue", out var value) || OwnOption(name)) continue;
            string kind = section[..dash].ToUpperInvariant();
            if (kind is "BOOL" or "INT" or "FLOAT" or "STRING" or "VECTOR3" && name.IndexOfAny(['\r', '\n', '=']) < 0 && value.IndexOfAny(['\r', '\n']) < 0) values.Add((kind, name, value));
        }
        return UpdateValues(own, [.. values]);
    }
    /// <summary>Options that stay each character's own when settings are shared.</summary>
    public static bool OwnOption(string name) => name.StartsWith("Graphics_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Sound_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Channel_", StringComparison.OrdinalIgnoreCase) || name.Equals("Current_Log_Page", StringComparison.OrdinalIgnoreCase);

    public static string UpdateValues(string text, (string Kind, string Name, string Value)[] values)
    {
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        foreach (var (kind, name, value) in values)
        {
            if (kind is not ("BOOL" or "INT" or "FLOAT" or "STRING" or "VECTOR3") || name.IndexOfAny(['\r', '\n', '=']) >= 0 || value.IndexOfAny(['\r', '\n']) >= 0)
                throw new ArgumentException("Invalid retail option.");
            bool found = false;
            for (int start = 0; start < lines.Count; start++)
            {
                if (!lines[start].Trim().StartsWith("[" + kind + "-", StringComparison.OrdinalIgnoreCase)) continue;
                int end = start + 1;
                while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
                if (!lines.Skip(start + 1).Take(end - start - 1).Any(l =>
                    Regex.IsMatch(l, @"^\s*OptionName\s*=\s*" + Regex.Escape(name) + @"\s*$", RegexOptions.IgnoreCase))) continue;
                bool wrote = false;
                for (int i = start + 1; i < end; i++)
                    if (Regex.IsMatch(lines[i], @"^\s*OptionValue\s*=", RegexOptions.IgnoreCase))
                    { lines[i] = "OptionValue=" + value; wrote = true; }
                if (!wrote) lines.Insert(end, "OptionValue=" + value);
                found = true;
            }
            if (!found)
            {
                int index = 0;
                while (lines.Any(l => l.Trim().Equals($"[{kind}-{index:000}]", StringComparison.OrdinalIgnoreCase))) index++;
                lines.AddRange(["", $"[{kind}-{index:000}]", "OptionName=" + name, "OptionValue=" + value]);
            }
        }
        return string.Join(newline, lines);
    }
}
