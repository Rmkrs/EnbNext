namespace EnbNext.Assets;

public enum EnbCurrentRetailRslEffectPassType
{
    Axial,
    Silhouette,
    Environment,
    Screen,
}

/// <summary>0045B550's Effect_Pass_Description reader. Axial and Silhouette
/// return no state; Screen reaches a null mapper dereference. Only Environment
/// constructs a usable current-retail material pass.</summary>
public sealed record EnbCurrentRetailRslEffectPassDefinition(
    EnbCurrentRetailRslEffectPassType Type, string Texture, uint PackedShader,
    bool TwoSided, bool Hidden)
{
    public bool ConstructsUsablePass => Type == EnbCurrentRetailRslEffectPassType.Environment;

    //0045B5F5..B625, with startup writes0093B2D0..B390.
    public static uint ShaderPreset(int selector) => selector switch
    {
        0 => 0x00094033,
        1 => 0x000980b3,
        2 => 0x000d401b,
        3 => 0x000d80bb,
        4 => 0x00094073,
        5 => 0x00090053,
        _ => 0x0008441b,
    };

    public static bool TryRead(EnbRetailIniDocument document,
        out EnbCurrentRetailRslEffectPassDefinition result, out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.TryGetSection("Effect_Pass_Description", out var values);
        string? Value(string key) => values is not null && values.TryGetValue(key, out var value) ? value : null;
        //009CB2E0 uses00A2A9A0: ASCII case folding, default enum zero.
        string? typeName = Value("EFFECT_PASS_TYPE");
        string? folded = typeName is null ? null : string.Create(typeName.Length, typeName, (chars, source) =>
        {
            for (int i = 0; i < chars.Length; i++)
                chars[i] = source[i] is >= 'a' and <= 'z' ? (char)(source[i] - 32) : source[i];
        });
        var type = folded switch
        {
            "SILHOUETTE" => EnbCurrentRetailRslEffectPassType.Silhouette,
            "ENVIRONMENT" => EnbCurrentRetailRslEffectPassType.Environment,
            "SCREEN" => EnbCurrentRetailRslEffectPassType.Screen,
            _ => EnbCurrentRetailRslEffectPassType.Axial,
        };
        if (!TryReadShader(Value("SHADER"), out int shader))
        {
            result = default!;
            diagnostic = "Effect-pass SHADER contains a failed native hexadecimal conversion.";
            return false;
        }
        string texture = Value("TEXTURE") ?? string.Empty;
        //009CAEA0 receives a0x104-byte destination, including terminator.
        if (texture.Length > 259) texture = texture[..259];
        result = new(type, texture, ShaderPreset(shader),
            ReadBoolean(Value("TWO_SIDED")), ReadBoolean(Value("HIDDEN")));
        diagnostic = null;
        return true;
    }

    //009CB860 dispatches solely on the uppercased first byte. All other
    //characters use the caller's false default, including an empty value.
    private static bool ReadBoolean(string? value)
        => value is { Length: > 0 } && value[0] is '1' or 'T' or 't' or 'Y' or 'y';

    private static bool TryReadShader(string? value, out int result)
    {
        result = 0;
        if (string.IsNullOrEmpty(value)) return true;
        bool hex = value[0] == '$' || value[^1] is 'h' or 'H';
        int index = value[0] == '$' ? 1 : 0;
        while (index < value.Length && value[index] is ' ' or '\t' or '\r' or '\n' or '\v' or '\f') index++;
        bool negative = index < value.Length && value[index] == '-';
        if (index < value.Length && value[index] is '+' or '-') index++;
        if (hex && index + 1 < value.Length && value[index] == '0' && value[index + 1] is 'x' or 'X') index += 2;
        int first = index;
        uint number = 0;
        while (index < value.Length)
        {
            char c = value[index];
            int digit = c is >= '0' and <= '9' ? c - '0' :
                hex && c is >= 'a' and <= 'f' ? c - 'a' + 10 :
                hex && c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
            if (digit < 0) break;
            number = unchecked(number * (hex ? 16u : 10u) + (uint)digit);
            index++;
        }
        //Decimal00A276D7 returns zero for a nonnumeric prefix. Failed %x
        //leaves an uninitialized native output: contain that resource locally.
        if (hex && index == first) return false;
        result = unchecked((int)(negative ? 0u - number : number));
        return true;
    }
}
