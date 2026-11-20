using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace EnbNext.Assets;

/// <summary>Retail output/chat.ini, current-character decimal retained-ID section.</summary>
public static class EnbRetailQuickMessages
{
    public static string Section(uint character) => BinaryPrimitives.ReverseEndianness(character).ToString(CultureInfo.InvariantCulture);

    public static string[] Load(string path, uint character)
    {
        var result = Enumerable.Repeat("", 9).ToArray();
        if (!EnbNext.Installation.EnbPathCase.FileExists(path)) return result;
        var ini = EnbRetailIniDocument.Parse(File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(path), Encoding.Latin1));
        if (ini.TryGetSection(Section(character), out var values))
            for (int i = 0; i < 9; i++)
                if (values.TryGetValue($"MSG_{i + 1}", out var value)) result[i] = value[..Math.Min(255, value.Length)];
        return result;
    }

    public static void Save(string path, uint character, int slot, string value)
    {
        if (character == 0) throw new ArgumentOutOfRangeException(nameof(character));
        if (slot is < 1 or > 9) throw new ArgumentOutOfRangeException(nameof(slot));
        // Retail uses a 256-byte C-string buffer. Do not reproduce its unterminated-buffer overflow.
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0 || value.Any(c => c > 255))
            throw new InvalidDataException("Quick messages must be a single line of retail-compatible text.");
        if (value.Length > 255) throw new InvalidDataException("Quick messages can contain at most 255 characters.");
        string section = Section(character), key = $"MSG_{slot}";
        var lines = EnbNext.Installation.EnbPathCase.FileExists(path) ? File.ReadAllLines(EnbNext.Installation.EnbPathCase.Resolve(path), Encoding.Latin1).ToList() : [];
        int start = lines.FindIndex(l => l.Trim().Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase));
        if (start < 0) { if (lines.Count > 0) lines.Add(""); start = lines.Count; lines.Add("[" + section + "]"); }
        int end = start + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
        bool replaced = false;
        for (int i = start + 1; i < end; i++)
        {
            int equals = lines[i].IndexOf('=');
            if (equals >= 0 && lines[i][..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            { lines[i] = key + "=" + value; replaced = true; }
        }
        if (!replaced) lines.Insert(end, key + "=" + value);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, string.Join("\r\n", lines) + "\r\n", Encoding.Latin1); File.Move(temporary, EnbNext.Installation.EnbPathCase.Resolve(path), true); }
        finally { if (EnbNext.Installation.EnbPathCase.FileExists(temporary)) File.Delete(temporary); }
    }
}
