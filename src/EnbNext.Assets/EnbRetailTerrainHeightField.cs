namespace EnbNext.Assets;

/// <summary>004637C0: byte-token P2 loading, row reversal and device-width reduction.</summary>
public sealed record EnbRetailTerrainHeightField(int Width, ushort[] Samples, ushort OriginalMaximum)
{
    public static EnbRetailTerrainHeightField Decode(ReadOnlySpan<byte> source, bool flip, int maximumWidth)
    {
        if (maximumWidth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumWidth));
        var reader = new Tokens(source);
        if (reader.Next().IndexOf("P2"u8) < 0)
            throw new InvalidDataException("Terrain height map has no P2 signature.");
        int width;
        do { width = Decimal(reader.Next()); } while (width == 0);
        int rows = Decimal(reader.Next());
        _ = reader.Next(); // Advertised maximum is read but does not control scaling.
        if (width <= 0 || rows != width || (long)width * width > Array.MaxLength ||
            (long)width * width > ((long)source.Length + 1) / 2)
            throw new InvalidDataException("Terrain height map dimensions cannot be stored.");
        var samples = new ushort[checked(width * width)];
        ushort maximum = 0;
        for (int y = 0; y < width; y++)
        for (int x = 0; x < width; x++)
        {
            ushort value = unchecked((ushort)Decimal(reader.Next()));
            samples[(flip ? width - 1 - y : y) * width + x] = value;
            maximum = Math.Max(maximum, value);
        }
        if (width <= maximumWidth) return new(width, samples, maximum);

        // 004639EF..00463AF4 retains the original maximum and averages integer
        // blocks after row reversal. Remainder rows/columns are not sampled.
        int factor = width / maximumWidth;
        uint divisor = (uint)(factor * factor);
        var reduced = new ushort[checked(maximumWidth * maximumWidth)];
        for (int y = 0; y < maximumWidth; y++)
        for (int x = 0; x < maximumWidth; x++)
        {
            uint sum = 0;
            for (int dy = 0; dy < factor; dy++)
            for (int dx = 0; dx < factor; dx++)
                sum = unchecked(sum + samples[(y * factor + dy) * width + x * factor + dx]);
            reduced[y * maximumWidth + x] = unchecked((ushort)(sum / divisor));
        }
        return new(maximumWidth, reduced, maximum);
    }

    // A27762 decimal prefix conversion; multiplication and sign wrap at 32 bits.
    private static int Decimal(ReadOnlySpan<byte> token)
    {
        int i = 0;
        while (i < token.Length && token[i] is 9 or 10 or 11 or 12 or 13 or 32) i++;
        bool negative = i < token.Length && token[i] == '-';
        if (i < token.Length && token[i] is (byte)'+' or (byte)'-') i++;
        uint value = 0;
        while (i < token.Length && token[i] is >= (byte)'0' and <= (byte)'9')
            value = unchecked(value * 10 + token[i++] - (byte)'0');
        return unchecked((int)(negative ? 0u - value : value));
    }

    private ref struct Tokens(ReadOnlySpan<byte> source)
    {
        private ReadOnlySpan<byte> remaining = source;
        public ReadOnlySpan<byte> Next()
        {
            int start = 0;
            while (start < remaining.Length && IsDelimiter(remaining[start])) start++;
            int end = start;
            while (end < remaining.Length && remaining[end] != 0 && !IsDelimiter(remaining[end])) end++;
            if (end == start) throw new InvalidDataException("Terrain height map is truncated.");
            var token = remaining[start..end];
            remaining = remaining[end..];
            return token;
        }
        private static bool IsDelimiter(byte value) => value is 9 or 10 or 13 or 32;
    }
}
