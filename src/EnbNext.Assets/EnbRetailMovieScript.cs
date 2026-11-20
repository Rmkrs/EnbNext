using System.Globalization;
using System.Text;

namespace EnbNext.Assets;

/// <summary>Current client 8099B0 / 7FFCB0 / 7FFEA0. Separate from Movie3D and WSD.</summary>
public sealed record EnbRetailMovieScript(
    IReadOnlyList<EnbRetailMovieCommand> Commands, string? Soundtrack, uint FadeAtMilliseconds)
{
    public static EnbRetailMovieScript Parse(ReadOnlySpan<byte> bytes)
    {
        var commands = new List<EnbRetailMovieCommand>();
        string? soundtrack = null;
        int pad = 0, lineNumber = 0;
        while (!bytes.IsEmpty)
        {
            lineNumber++;
            int lf = bytes.IndexOf((byte)'\n');
            // Contain native buffer overruns locally. These are storage bounds,
            // not additional command/asset admission rules.
            if (lf < 0 || lf >= 512)
                throw new InvalidDataException($"Movie line {lineNumber} exceeds its terminated native line buffer.");
            // 809B04..0D writes NUL at count-1, including for LF-only input.
            string line = Encoding.Latin1.GetString(bytes[..Math.Max(0, lf - 1)]).Replace('\t', ' ');
            bytes = bytes[(lf + 1)..];
            int zero = line.IndexOf('\0');
            if (zero >= 0) line = line[..zero];
            line = Trim(line);
            if (line.Length == 0 || line[0] == ';') continue;
            int frame = EnbRetailMovieArguments.Integer(line);
            if (frame < 0) frame = unchecked(-frame);
            int start = 0;
            while (start < line.Length && line[start] > 32) start++;
            while (start < line.Length && line[start] <= 32) start++;
            string text = line[start..];
            if (text.Length == 0) continue;
            if (text.StartsWith("FRAME_PAD", StringComparison.OrdinalIgnoreCase))
            {
                pad = EnbRetailMovieArguments.Integer(text.Length > 10 ? text[10..] : "");
                continue;
            }
            if (text.StartsWith("SOUNDTRACK", StringComparison.OrdinalIgnoreCase))
            {
                var args = new EnbRetailMovieArguments(text);
                args.Read();
                soundtrack = args.Read();
                continue;
            }
            uint time = frame == 0 && text.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase)
                ? 0 : unchecked((uint)(unchecked((frame + pad) * 1000) / 30));
            commands.Add(new(time, EnbRetailMovieArguments.Upper(text), lineNumber));
        }
        var result = commands.ToArray();
        Partition(result, 0, result.Length);
        // 8099B0: guarded insertion of first sixteen then unguarded remainder.
        // A bounds check here produces the same order for the proven comparator.
        for (int i = 1; i < result.Length; i++)
        {
            var item = result[i];
            int j = i;
            while (j > 0 && Less(item, result[j - 1])) { result[j] = result[j - 1]; j--; }
            result[j] = item;
        }
        return new(result, soundtrack, unchecked((uint)(unchecked(pad * 1000) / 30)));
    }

    private static string Trim(string text)
    {
        int first = 0, last = text.Length;
        while (first < last && text[first] <= 32) first++;
        while (last > first && text[last - 1] <= 32) last--;
        return text[first..last];
    }

    private static bool Less(EnbRetailMovieCommand a, EnbRetailMovieCommand b)
        => a.Milliseconds != b.Milliseconds ? a.Milliseconds < b.Milliseconds : Priority(a.Text) < Priority(b.Text);

    private static int Priority(string text)
    {
        if (text.StartsWith("CREATE_OBJECT", StringComparison.Ordinal)) return 0;
        if (text.StartsWith("CREATE_SHIP_OBJECT", StringComparison.Ordinal)) return 1;
        if (text.StartsWith("CREATE_POINT_LIGHT", StringComparison.Ordinal)) return 2;
        if (text.StartsWith("CREATE_DIRECTIONAL_LIGHT", StringComparison.Ordinal)) return 3;
        return 4;
    }

    // 80B160, also inlined at 80A0B0: median-of-three partition; recurse on
    // smaller side, leave <=16 unsorted. Equivalent commands can exchange places.
    private static void Partition(EnbRetailMovieCommand[] a, int first, int last)
    {
        while (last - first > 16)
        {
            var x = a[first]; var y = a[first + (last - first) / 2]; var z = a[last - 1];
            var pivot = Less(x, y) ? (Less(y, z) ? y : Less(x, z) ? z : x)
                : Less(x, z) ? x : Less(y, z) ? z : y;
            int left = first, right = last;
            while (true)
            {
                while (Less(a[left], pivot)) left++;
                do { right--; } while (Less(pivot, a[right]));
                if (right <= left) break;
                (a[left], a[right]) = (a[right], a[left]);
                left++;
            }
            if (left - first < last - left) { Partition(a, first, left); first = left; }
            else { Partition(a, left, last); last = left; }
        }
    }
}

public sealed record EnbRetailMovieCommand(uint Milliseconds, string Text, int LineNumber);

/// <summary>Mutable command cursor: prefix recognition and comma/quote semantics at 7FFD30/7FFDC0.</summary>
public sealed class EnbRetailMovieArguments
{
    private readonly string _text;
    private int _cursor;
    public EnbRetailMovieArguments(string text) => _text = Upper(text.Split('\0')[0]);

    public bool Match(string token)
    {
        if (!_text.AsSpan(_cursor).StartsWith(token, StringComparison.Ordinal)) return false;
        _cursor += token.Length;
        while (_cursor < _text.Length && _text[_cursor] != ',') _cursor++;
        if (_cursor < _text.Length) _cursor++;
        return true;
    }

    public string? Read()
    {
        if (_cursor >= _text.Length) return null;
        while (_cursor < _text.Length && _text[_cursor] <= 32) _cursor++;
        int first = _cursor;
        if (_cursor < _text.Length && _text[_cursor] == '"')
        {
            first = ++_cursor;
            while (_cursor < _text.Length && _text[_cursor] != '"') _cursor++;
        }
        while (_cursor < _text.Length && _text[_cursor] != ',') _cursor++;
        int last = _cursor;
        if (_cursor < _text.Length) _cursor++;
        while (last > first && _text[last - 1] <= 32) last--;
        if (last > first && _text[last - 1] == '"') last--;
        return _text[first..last];
    }

    internal static string Upper(string value) => string.Create(value.Length, value, static (span, source) =>
    {
        for (int i = 0; i < source.Length; i++) span[i] = source[i] is >= 'a' and <= 'z' ? (char)(source[i] - 32) : source[i];
    });

    public static int Integer(string value)
    {
        int i = 0; while (i < value.Length && char.IsAscii(value[i]) && char.IsWhiteSpace(value[i])) i++;
        bool negative = i < value.Length && value[i] == '-';
        if (i < value.Length && value[i] is '+' or '-') i++;
        uint result = 0;
        while (i < value.Length && value[i] is >= '0' and <= '9') result = unchecked(result * 10 + (uint)(value[i++] - '0'));
        return negative ? unchecked(-(int)result) : unchecked((int)result);
    }

    public static float Number(string value)
    {
        int first = 0; while (first < value.Length && char.IsAscii(value[first]) && char.IsWhiteSpace(value[first])) first++;
        int i = first;
        if (i < value.Length && value[i] is '+' or '-') i++;
        int digits = 0;
        while (i < value.Length && char.IsAsciiDigit(value[i])) { i++; digits++; }
        if (i < value.Length && value[i] == '.')
        {
            i++;
            while (i < value.Length && char.IsAsciiDigit(value[i])) { i++; digits++; }
        }
        if (digits == 0) return 0;
        int end = i;
        if (i < value.Length && value[i] is 'e' or 'E')
        {
            i++;
            if (i < value.Length && value[i] is '+' or '-') i++;
            int exponent = i;
            while (i < value.Length && char.IsAsciiDigit(value[i])) i++;
            if (i > exponent) end = i;
        }
        return (float)double.Parse(value.AsSpan(first, end - first), NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
