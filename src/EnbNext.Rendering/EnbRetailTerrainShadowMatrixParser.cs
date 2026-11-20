using System.Globalization;
using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>0046E781..0046E848: comma-only strtok tokens fill the four columns of
/// a 3x4 matrix in order; tokens that are absent keep identity. Each token is
/// converted like the CRT's atof.</summary>
public static class EnbRetailTerrainShadowMatrixParser
{
    public static Matrix4x4 Parse(ReadOnlySpan<byte> value)
    {
        Span<float> matrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];
        int nul = value.IndexOf((byte)0); if (nul >= 0) value = value[..nul];
        int position = 0;
        for (int column = 0; column < 4; column++)
            for (int row = 0; row < 3; row++)
            {
                while (position < value.Length && value[position] == ',') position++;
                if (position == value.Length) continue;
                int begin = position;
                while (position < value.Length && value[position] != ',') position++;
                matrix[row * 4 + column] = (float)Atof(value[begin..position]);
            }
        return new(matrix[0], matrix[4], matrix[8], 0, matrix[1], matrix[5], matrix[9], 0,
            matrix[2], matrix[6], matrix[10], 0, matrix[3], matrix[7], matrix[11], 1);
    }

    /// <summary>CRT atof: leading white space, sign, digits with an optional point,
    /// then an optional exponent (e, E, d or D); parsing stops at the first other byte.</summary>
    public static double Atof(ReadOnlySpan<byte> text)
    {
        int i = 0;
        while (i < text.Length && (text[i] == ' ' || (text[i] >= 9 && text[i] <= 13))) i++;
        Span<char> number = stackalloc char[Math.Min(text.Length - i, 512) + 1];
        int n = 0;
        if (i < text.Length && (text[i] == '+' || text[i] == '-')) Put(number, ref n, text[i++]);
        int digits = 0;
        while (i < text.Length && IsDigit(text[i])) { Put(number, ref n, text[i++]); digits++; }
        if (i < text.Length && text[i] == '.')
        {
            Put(number, ref n, text[i++]);
            while (i < text.Length && IsDigit(text[i])) { Put(number, ref n, text[i++]); digits++; }
        }
        if (digits == 0) return 0;
        if (i < text.Length && (text[i] | 0x20) is (byte)'e' or (byte)'d')
        {
            int e = i + 1;
            if (e < text.Length && (text[e] == '+' || text[e] == '-')) e++;
            if (e < text.Length && IsDigit(text[e]))
            {
                Put(number, ref n, (byte)'e');
                for (int k = i + 1; k < e; k++) Put(number, ref n, text[k]);
                while (e < text.Length && IsDigit(text[e])) Put(number, ref n, text[e++]);
            }
        }
        return double.Parse(number[..n], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static void Put(Span<char> number, ref int n, byte b) { if (n < number.Length) number[n++] = (char)b; }
    private static bool IsDigit(byte b) => b >= '0' && b <= '9';
}
