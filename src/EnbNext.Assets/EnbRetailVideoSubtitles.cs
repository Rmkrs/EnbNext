using System.Text;

namespace EnbNext.Assets;

public sealed record EnbRetailVideoSubtitle(uint Time, uint Duration, uint Color, int Row, int Alignment, string? Text);

/// <summary>A17300/A17340/A17690, independent of the scripted-movie format.</summary>
public static class EnbRetailVideoSubtitles
{
    public static IReadOnlyList<EnbRetailVideoSubtitle> Parse(ReadOnlySpan<byte> source, string movieFile)
    {
        if (source.Length < 2) return [];
        // Native consumes two bytes without checking a BOM, then reads UTF16LE.
        string text = Encoding.Unicode.GetString(source[2..]);
        string key = Path.GetFileNameWithoutExtension(movieFile.Replace('\\', '/'));
        if (key.Length > 32) key = key[..32];
        bool found = false;
        List<EnbRetailVideoSubtitle> result = [];
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            if (raw.Length >= 1024) throw new InvalidDataException("Subtitle line exceeds the retail reader capacity.");
            string line = Trim(raw.Split('\0')[0]);
            if (line.Length == 0 || line[0] == ';') continue;
            if (!found)
            {
                int space = line.IndexOf(' ');
                found = line.StartsWith("BeginMovie", StringComparison.OrdinalIgnoreCase) && space >= 0 &&
                    Trim(line[space..]).Equals(key, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (line.StartsWith("EndMovie", StringComparison.OrdinalIgnoreCase)) return result;
            if (line[0] == '<' && line[^1] == '>' && TryRecord(Trim(line[1..^1]), out var item)) result.Add(item!);
        }
        // A1747D loops forever on a missing terminator. Contain a malformed
        // optional subtitle file instead of stalling the client's video task.
        if (found) throw new InvalidDataException("Subtitle movie block has no EndMovie.");
        return [];
    }

    private static bool TryRecord(string fields, out EnbRetailVideoSubtitle? result)
    {
        result = null;
        uint time = 0, duration = 1200, color = 0x00ffffff;
        int row = 15, alignment = 2;
        string? text = null;
        do
        {
            int equals = fields.IndexOf('=');
            if (equals < 0) return false;
            string name = Trim(fields[..equals]), tail = Trim(fields[(equals + 1)..]), value;
            if (name.Length == 0) return false;
            if (tail.StartsWith('"'))
            {
                int end = tail.IndexOf('"', 1);
                if (end < 0) return false;
                value = tail[1..end]; fields = Trim(tail[(end + 1)..]);
                if (fields.StartsWith(',')) fields = Trim(fields[1..]);
            }
            else
            {
                int end = tail.IndexOfAny([',', ' ']);
                value = end < 0 ? tail : tail[..end]; fields = end < 0 ? "" : Trim(tail[(end + 1)..]);
            }
            switch (name.ToUpperInvariant())
            {
                case "TIME": if (!TryTime(value, out time)) return false; break;
                case "DURATION":
                    if (!TryTime(value, out uint parsed)) return false;
                    if (parsed > 0) duration = parsed;
                    break;
                case "POSITION":
                    int colon = value.IndexOf(':');
                    if (colon >= 0) { row = unchecked((int)Integer(value[..colon], true)); value = value[(colon + 1)..]; }
                    alignment = value.Equals("Left", StringComparison.OrdinalIgnoreCase) ? 0 : value.Equals("Right", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                    break;
                case "COLOR":
                    var rgb = value.Split(':');
                    if (rgb.Length < 3) return false;
                    color = ((Integer(rgb[0]) & 255) << 16) | ((Integer(rgb[1]) & 255) << 8) | (Integer(rgb[2]) & 255);
                    break;
                case "TEXT": text = value; break;
            }
        } while (fields.Length != 0);
        result = new(time, duration, color, row, alignment, text);
        return true;
    }

    public static bool TryTime(string value, out uint ticks)
    {
        string[] parts = value[..Math.Min(11, value.Length)].Split(':');
        ticks = 0;
        if (parts.Length < 4) return false;
        ticks = unchecked(60 * (Integer(parts[2]) + 60 * (Integer(parts[1]) + 60 * Integer(parts[0]))) + unchecked(60 * Integer(parts[3])) / 100);
        return true;
    }

    private static string Trim(string value)
    {
        int first = 0, end = value.Length;
        while (first < end && value[first] <= ' ') first++;
        while (end > first && value[end - 1] <= ' ') end--;
        return value[first..end];
    }

    private static uint Integer(string value, bool autoBase = false)
    {
        value = Trim(value); int index = 0; bool negative = value.StartsWith('-');
        if (value.StartsWith('+') || negative) index++;
        int radix = 10;
        if (autoBase && index < value.Length && value[index] == '0')
        {
            radix = 8;
            if (index + 1 < value.Length && (value[index + 1] is 'x' or 'X')) { radix = 16; index += 2; }
        }
        uint number = 0;
        for (; index < value.Length; index++)
        {
            char c = value[index]; int digit = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : 99;
            if (digit >= radix) break;
            number = unchecked(number * (uint)radix + (uint)digit);
        }
        return negative ? unchecked(0u - number) : number;
    }
}
