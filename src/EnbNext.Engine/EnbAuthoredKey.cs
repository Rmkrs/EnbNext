namespace EnbNext.Engine;

/// <summary>0042BC90 authored key-name table; ordinal byte identity (0042D8A0).
/// This is the retail input vocabulary, not a platform key conversion.</summary>
internal static class EnbAuthoredKey
{
    public static ushort Code(string name) => EnbNext.Assets.EnbRetailKeyIdentity.Code(name);

    // 0042A1A0 prefix parsing followed by 00587D50 dialog modifier override.
    public static (ushort Code, ushort Modifiers) Dialog(string value)
    {
        if (value.Length > 48) value = value[..48];
        if (value == "~") value = "`";
        ushort modifiers = 0;
        int offset = 0;
        while (offset < value.Length)
        {
            ushort bit = value[offset] switch { '$' => 0x100, '^' => 0x200, '@' => 0x400, _ => 0 };
            if (bit == 0) break;
            modifiers |= bit; offset++;
        }
        ushort code = Code(value[offset..]);
        if (offset == value.Length) return default;
        if (code is not (0x1b or 0x0d)) modifiers = 0x400;
        return (code, modifiers);
    }
}
