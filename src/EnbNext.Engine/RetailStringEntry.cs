namespace EnbNext.Engine;

internal static class RetailStringEntry
{
    //004BE9D0, called by00649FE0 when the dialog's CD flag is set.
    //The retail C locale changes ASCII case only. Bulk insertion is separate
    //from0084AAD0's typed-key alpha mask and may contain punctuation.
    internal static string NormalizeName(string? value)
    {
        char[] text = (value ?? "").TakeWhile(c => c != '\0').Where(c => c <= 255).Take(16).ToArray();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (i == 0 && c is >= 'a' and <= 'z') text[i] = (char)(c - 32);
            else if (i != 0 && c is >= 'A' and <= 'Z') text[i] = (char)(c + 32);
        }
        return new string(text);
    }
}
