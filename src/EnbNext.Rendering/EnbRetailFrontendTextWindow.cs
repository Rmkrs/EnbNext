namespace EnbNext.Rendering;

/// <summary>The focused 0x400 text-gadget branch at 006AADF3..006AAE90.</summary>
public static class EnbRetailFrontendTextWindow
{
    public static string TrimLeading(string text, float width, Func<string, float> measure)
    {
        if (measure(text) < width) return text;
        int start = 0;
        while (start < text.Length && measure(text[start..]) > width) start++;
        // Retail only memmoves when the byte following the candidate is nonzero.
        return start + 1 < text.Length ? text[start..] : text;
    }
}
