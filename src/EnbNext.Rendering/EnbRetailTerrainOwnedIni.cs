using System.Text;

namespace EnbNext.Rendering;

/// <summary>Terrain INI (landNNNN.ini, decoration.ini) with the retail client's reading rules:
/// case-sensitive names; ';' starts a comment; empty values are absent; a section exists once it
/// has a value and a repeated name keeps its first such occurrence; integers are C-style decimal or,
/// with a trailing 'h', hexadecimal; floats with a '%' are scaled by 0.01; booleans go by the first
/// character (T/Y/1, F/N/0). Checked against the previous translated parser over every INI in the
/// installed game: identical for all terrain INIs.</summary>
public sealed class EnbRetailTerrainOwnedIni : IDisposable
{
    private readonly Dictionary<string, Dictionary<string, byte[]>> sections = new(StringComparer.Ordinal);
    public IReadOnlyList<string> Diagnostics => [];
    public bool LoadSucceeded { get; }

    public EnbRetailTerrainOwnedIni(ReadOnlySpan<byte> bytes)
    {
        // A section exists once it has a key with a non-empty value. When a name repeats,
        // only the first occurrence that has keys counts; empty values are not stored.
        string? pending = null; Dictionary<string, byte[]>? current = null; bool ignore = false;
        foreach (var raw in Lines(bytes))
        {
            var line = Trim(raw);
            if (line.Length == 0 || line[0] == (byte)';') continue;
            if (line[0] == (byte)'[')
            {
                int end = line.IndexOf((byte)']');
                pending = Latin1(Trim(end < 0 ? line[1..] : line[1..end]));
                current = null; ignore = sections.ContainsKey(pending);
                continue;
            }
            int eq = line.IndexOf((byte)'=');
            if (pending is null || ignore || eq < 0) continue;
            var value = line[(eq + 1)..];
            int comment = value.IndexOf((byte)';');
            if (comment >= 0) value = value[..comment];
            value = Trim(value);
            if (value.Length == 0) continue;
            if (current is null) sections[pending] = current = new(StringComparer.Ordinal);
            current.TryAdd(Latin1(Trim(line[..eq])), value.ToArray());
        }
        LoadSucceeded = sections.Count > 0;
    }

    public bool HasSection(string section) => sections.ContainsKey(section);

    private byte[]? Value(string section, string key) =>
        sections.TryGetValue(section, out var keys) && keys.TryGetValue(key, out var value) ? value : null;

    public byte[] ReadText(string section, string key, int capacity, string defaultValue = "")
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        ReadOnlySpan<byte> value = Value(section, key) ?? Encoding.Latin1.GetBytes(defaultValue);
        if (value.Length >= capacity) value = Trim(value[..(capacity - 1)]);
        return value.ToArray();
    }

    public int ReadInteger(string section, string key, int defaultValue) =>
        Value(section, key) is { } value ? Integer(value) ?? defaultValue : defaultValue;

    public float ReadFloat(string section, string key, float defaultValue,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24) =>
        Float(Value(section, key), defaultValue);

    // A '%' anywhere scales the value (or, without a number, the default) by 0.01.
    private static float Float(byte[]? value, float defaultValue)
    {
        if (value is null) return defaultValue;
        float f = LeadingDouble(value) is { } d ? (float)d : defaultValue;
        return value.AsSpan().IndexOf((byte)'%') >= 0 ? f * 0.01f : f;
    }

    public bool ReadBoolean(string section, string key, bool defaultValue)
    {
        if (Value(section, key) is not { } value) return defaultValue;
        if (value.Length == 0) return defaultValue;
        return char.ToUpperInvariant((char)value[0]) switch { 'T' or 'Y' or '1' => true, 'F' or 'N' or '0' => false, _ => defaultValue };
    }

    public void Dispose() { }

    private static IEnumerable<byte[]> Lines(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<byte[]>(); int start = 0;
        for (int i = 0; i <= bytes.Length; i++)
            if (i == bytes.Length || bytes[i] == (byte)'\n') { var l = bytes[start..i]; if (l.Length > 0 && l[^1] == (byte)'\r') l = l[..^1]; lines.Add(l.ToArray()); start = i + 1; }
        return lines;
    }
    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> s)
    {
        int a = 0, b = s.Length;
        while (a < b && s[a] is (byte)' ' or (byte)'\t') a++;
        while (b > a && s[b - 1] is (byte)' ' or (byte)'\t') b--;
        return s[a..b];
    }
    private static string Latin1(ReadOnlySpan<byte> s) => Encoding.Latin1.GetString(s);

    // A trailing 'h' marks hexadecimal ("1Fh"); without hex digits the default stays.
    // Otherwise the leading decimal number, 0 when there is none. Both wrap at 32 bits.
    private static int? Integer(ReadOnlySpan<byte> s)
    {
        bool hex = s.Length > 0 && s[^1] is (byte)'h' or (byte)'H';
        if (hex) s = s[..^1];
        int i = 0; while (i < s.Length && s[i] is (byte)' ' or (byte)'\t') i++;
        bool negative = false; if (i < s.Length && s[i] is (byte)'+' or (byte)'-') negative = s[i++] == (byte)'-';
        uint v = 0; int digits = 0;
        if (hex)
        {
            if (i + 1 < s.Length && s[i] == (byte)'0' && s[i + 1] is (byte)'x' or (byte)'X') i += 2;
            for (; i < s.Length; i++, digits++)
            {
                int d = s[i] switch { >= (byte)'0' and <= (byte)'9' => s[i] - '0', >= (byte)'a' and <= (byte)'f' => s[i] - 'a' + 10, >= (byte)'A' and <= (byte)'F' => s[i] - 'A' + 10, _ => -1 };
                if (d < 0) break;
                v = unchecked(v * 16 + (uint)d);
            }
            if (digits == 0) return null;
        }
        else for (; i < s.Length && s[i] is >= (byte)'0' and <= (byte)'9'; i++) v = unchecked(v * 10 + (uint)(s[i] - '0'));
        return unchecked((int)(negative ? 0u - v : v));
    }
    private static double? LeadingDouble(ReadOnlySpan<byte> s)
    {
        int i = 0; while (i < s.Length && s[i] is (byte)' ' or (byte)'\t') i++;
        int start = i; if (i < s.Length && s[i] is (byte)'+' or (byte)'-') i++;
        while (i < s.Length && s[i] is >= (byte)'0' and <= (byte)'9') i++;
        if (i < s.Length && s[i] == (byte)'.') { i++; while (i < s.Length && s[i] is >= (byte)'0' and <= (byte)'9') i++; }
        if (i < s.Length && s[i] is (byte)'e' or (byte)'E')
        {
            int e = i + 1; if (e < s.Length && s[e] is (byte)'+' or (byte)'-') e++;
            if (e < s.Length && s[e] is >= (byte)'0' and <= (byte)'9') { i = e; while (i < s.Length && s[i] is >= (byte)'0' and <= (byte)'9') i++; }
        }
        return double.TryParse(Encoding.ASCII.GetString(s[start..i]), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
