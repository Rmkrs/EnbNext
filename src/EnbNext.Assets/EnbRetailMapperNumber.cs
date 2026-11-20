using System.Globalization;

namespace EnbNext.Assets;

/// <summary>Numeric-prefix float semantics of client.exe 009CA770 (%f scan).</summary>
public static class EnbRetailMapperNumber
{
    public static bool TryParse(string text, out float value)
    {
        if(!TryParseOperand(text,out value,out bool percentage))return false;
        if(percentage)value*=.01f;
        return float.IsFinite(value);
    }

    /// <summary>The float written by the %f scan, before009CA8CA's retained
    /// percentage multiplication. Constructor consumers can retain that operand.</summary>
    public static bool TryParseOperand(string text,out float value,out bool percentage, bool requireFinite = true)
    {
        percentage=text.Contains('%');
        ReadOnlySpan<char> numeric = text.AsSpan().TrimStart();
        int end = 0;
        if (end < numeric.Length && numeric[end] is '+' or '-') end++;
        int digits = 0;
        while (end < numeric.Length && char.IsAsciiDigit(numeric[end])) { end++; digits++; }
        if (end < numeric.Length && numeric[end] == '.')
        {
            end++;
            while (end < numeric.Length && char.IsAsciiDigit(numeric[end])) { end++; digits++; }
        }
        if (digits == 0) { value = 0; return false; }
        if (end < numeric.Length && numeric[end] is 'e' or 'E')
        {
            int exponent = end + 1;
            if (exponent < numeric.Length && numeric[exponent] is '+' or '-') exponent++;
            int firstDigit = exponent;
            while (exponent < numeric.Length && char.IsAsciiDigit(numeric[exponent])) exponent++;
            if (exponent > firstDigit) end = exponent;
        }
        if (!float.TryParse(numeric[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            (requireFinite && !float.IsFinite(value))) return false;
        return true;
    }
}
