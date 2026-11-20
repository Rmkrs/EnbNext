using System.Globalization;

namespace EnbNext.Assets;

/// <summary>
/// Small deterministic INI reader for retail Earth &amp; Beyond presentation data.
/// Section and key lookup are case-insensitive, duplicate keys use the last
/// value encountered, and bare keys are preserved with an empty value.
/// </summary>
public sealed class EnbRetailIniDocument
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _sections;

    private EnbRetailIniDocument(Dictionary<string, IReadOnlyDictionary<string, string>> sections)
    {
        _sections = sections;
    }

    public int SectionCount => _sections.Count;

    public IEnumerable<string> SectionNames => _sections.Keys;

    public static EnbRetailIniDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(path)));
    }

    public static EnbRetailIniDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var mutable = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;

        using var reader = new StringReader(text);
        string? raw;
        while ((raw = reader.ReadLine()) is not null)
        {
            string line = raw.Trim();

            // Current retail IniDocument parsing strips the first ';' and the
            // remainder of the physical line before it looks for '='. This is
            // not just a whole-line comment rule: authored values such as
            // "SkyEmissive.Red Ambient; comment" become exactly
            // "SkyEmissive.Red Ambient". Keep '#' as the existing EnbNext
            // compatibility comment form, but reproduce retail ';' semantics.
            int semicolon = line.IndexOf(';');
            if (semicolon >= 0)
                line = line[..semicolon].TrimEnd();

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (TryParseSectionHeader(line, out string sectionName))
            {
                if (!mutable.TryGetValue(sectionName, out current))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    mutable.Add(sectionName, current);
                }
                continue;
            }

            if (current is null)
                continue;

            int equals = line.IndexOf('=');
            if (equals < 0)
            {
                current[line] = string.Empty;
                continue;
            }

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();
            if (key.Length != 0)
                current[key] = value;
        }

        var frozen = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, Dictionary<string, string> values) in mutable)
            frozen[name] = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);

        return new EnbRetailIniDocument(frozen);
    }


    private static bool TryParseSectionHeader(string line, out string sectionName)
    {
        sectionName = string.Empty;
        if (line.Length < 2 || line[0] != '[')
            return false;

        int close = line.IndexOf(']');
        if (close <= 1)
            return false;

        string trailing = line[(close + 1)..].TrimStart();
        if (trailing.Length != 0 && !trailing.StartsWith(';') && !trailing.StartsWith('#'))
            return false;

        sectionName = line[1..close].Trim();
        return sectionName.Length != 0;
    }

    public bool TryGetSection(string name, out IReadOnlyDictionary<string, string> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_sections.TryGetValue(name, out IReadOnlyDictionary<string, string>? found))
        {
            values = found;
            return true;
        }

        values = default!;
        return false;
    }

    public IEnumerable<KeyValuePair<string, IReadOnlyDictionary<string, string>>> EnumerateSections()
        => _sections;

    public static bool ReadBool(IReadOnlyDictionary<string, string> values, string key)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!values.TryGetValue(key, out string? raw))
            return false;
        if (raw.Length == 0)
            return true;

        return raw.Trim() switch
        {
            "1" => true,
            "0" => false,
            var value when value.Equals("T", StringComparison.OrdinalIgnoreCase) => true,
            var value when value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) => true,
            var value when value.Equals("YES", StringComparison.OrdinalIgnoreCase) => true,
            var value when value.Equals("ON", StringComparison.OrdinalIgnoreCase) => true,
            _ => false,
        };
    }

    public static bool TryReadInt(IReadOnlyDictionary<string, string> values, string key, out int value)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        value = 0;
        return values.TryGetValue(key, out string? raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>009CA1C0's present-value conversion in the default C locale:
    /// decimal prefixes, $ hexadecimal and trailing h hexadecimal, with DWORD wrap.</summary>
    public static int ReadCurrentRetailInteger(IReadOnlyDictionary<string, string> values, string key, int fallback)
    {
        if (!values.TryGetValue(key, out string? raw)) return fallback;
        int end = raw.IndexOf('\0');
        if (end < 0) end = raw.Length;
        bool dollar = end > 0 && raw[0] == '$';
        bool hex = dollar || end > 0 && raw[end - 1] is 'h' or 'H';
        int index = dollar ? 1 : 0;
        while (index < end && raw[index] is ' ' or '\t' or '\n' or '\r' or '\v' or '\f') index++;
        bool negative = index < end && raw[index] == '-';
        if (index < end && raw[index] is '+' or '-') index++;
        if (hex && index + 1 < end && raw[index] == '0' && raw[index + 1] is 'x' or 'X') index += 2;
        uint result = 0;
        bool any = false;
        for (; index < end; index++)
        {
            char c = raw[index];
            int digit = c is >= '0' and <= '9' ? c - '0' :
                c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
            if (digit < 0 || digit >= (hex ? 16 : 10)) break;
            result = unchecked(result * (hex ? 16u : 10u) + (uint)digit);
            any = true;
        }
        if (hex && !any) return fallback;
        return unchecked((int)(negative ? 0u - result : result));
    }

    public static bool TryReadFloat(IReadOnlyDictionary<string, string> values, string key, out float value)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        value = 0;
        return values.TryGetValue(key, out string? raw)
            && TryParseFloat(raw, out value);
    }

    public static bool TryParseFloat(string? raw, out float value)
    {
        value = 0;
        return raw is not null
            && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryParseSectionId(string sectionName, string prefix, out int id)
    {
        ArgumentNullException.ThrowIfNull(sectionName);
        ArgumentNullException.ThrowIfNull(prefix);

        id = 0;
        if (!sectionName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return int.TryParse(
            sectionName.AsSpan(prefix.Length),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out id);
    }
}
