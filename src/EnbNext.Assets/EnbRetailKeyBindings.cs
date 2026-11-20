using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;

namespace EnbNext.Assets;

[Flags]
public enum EnbKeyModifiers { None = 0, Alt = 1, Control = 2, Shift = 4 }

public sealed record EnbRetailKeyBinding(string Action, string Key, EnbKeyModifiers Modifiers)
{
    public int Slot { get; init; } = 1;
    // Live capture can retain a numeric code without a registered display name (0042B6A0).
    public ushort? CapturedCode { get; init; }
    public ushort Code => CapturedCode ?? EnbRetailKeyIdentity.Code(Key);
    public string Encoded => (Modifiers.HasFlag(EnbKeyModifiers.Alt) ? "@" : "") +
        (Modifiers.HasFlag(EnbKeyModifiers.Control) ? "^" : "") +
        (Modifiers.HasFlag(EnbKeyModifiers.Shift) ? "$" : "") + Key;
    public string Display => (Modifiers.HasFlag(EnbKeyModifiers.Control) ? "Ctrl-" : "") +
        (Modifiers.HasFlag(EnbKeyModifiers.Alt) ? "Alt-" : "") +
        (Modifiers.HasFlag(EnbKeyModifiers.Shift) ? "Shift-" : "") + Key;
}

/// <summary>Current retail control_keys.ini / output/keymap.ini selection (0071A3F0, 0071BD90).
/// A saved section replaces the complete map, including intentionally unbound actions.</summary>
public sealed record EnbRetailKeyBindings(string SourceSection, ImmutableArray<EnbRetailKeyBinding> Bindings)
{
    public static int ShortcutSlot(string? action) => action switch
    {
        "Use Slot 1" => 0, "Use Slot 2" => 1, "Use Slot 3" => 2,
        "Use Slot 4" => 3, "Use Slot 5" => 4, "Use Slot 6" => 5,
        "Use Slot 7" => 6, "Use Slot 8" => 7, "Use Slot 9" => 8, "Use Slot 10" => 9, "Use Slot 11" => 10, "Use Slot 12" => 11, _ => -1,
    };

    /// <param name="modern">Modern UI: its own controls count and each slot has its own key (no Alt bank chords);
    /// otherwise the retail resolution without the Modern UI controls.</param>
    public string? ResolveAction(Func<string,bool> matchesKey, EnbKeyModifiers modifiers, bool modern = false)
    {
        var exact = Bindings.FirstOrDefault(b => b.Modifiers == modifiers && EnbNextControls.Shown(b.Action, modern) && matchesKey(b.Key));
        if (exact is not null) return exact.Action;
        if (modern) return null;
        // 00611300 / 0071A960 register each configured slot chord both
        // normally and with Alt (0x400) ORed in. No numeric-key fallback.
        return Bindings.FirstOrDefault(b => ShortcutSlot(b.Action) is >= 0 and < 6 &&
            (b.Modifiers | EnbKeyModifiers.Alt) == modifiers && matchesKey(b.Key))?.Action;
    }

    public static EnbRetailKeyBindings Load(EnbInstalledAssetPaths paths, uint? fixedSessionId)
    {
        string definitions = Path.Combine(paths.ClientIniDirectory, "control_keys.ini");
        string saved = Path.Combine(paths.ClientOutputDirectory, "keymap.ini");
        return Resolve(EnbNext.Installation.EnbPathCase.FileExists(definitions) ? EnbRetailIniDocument.Load(definitions) : EnbRetailIniDocument.Parse(""),
            EnbNext.Installation.EnbPathCase.FileExists(saved) ? EnbRetailIniDocument.Load(saved) : EnbRetailIniDocument.Parse(""), fixedSessionId);
    }

    public static EnbRetailKeyBindings Resolve(EnbRetailIniDocument definitions, EnbRetailIniDocument saved, uint? fixedSessionId)
    {
        // Unlike player_options, the key-map name formats the entire retained ID.
        string? section = fixedSessionId is uint id && id != 0
            ? BinaryPrimitives.ReverseEndianness(id).ToString(CultureInfo.InvariantCulture) : null;
        if (section is null || !saved.TryGetSection(section, out _))
            section = saved.TryGetSection("Default", out var defaults) && defaults.TryGetValue("User", out var user) ? user : null;
        var result = ImmutableArray.CreateBuilder<EnbRetailKeyBinding>();
        if (!string.IsNullOrWhiteSpace(section) && saved.TryGetSection(section, out var map))
        {
            foreach (var (name, value) in map)
            {
                int separator = name.IndexOf('_');
                if (separator < 1 || !int.TryParse(name.AsSpan(0, separator), out int slot) || slot is < 1 or > 2) continue;
                string action = name[(separator + 1)..];
                if (!action.StartsWith('-') && definitions.TryGetSection(action, out var definition) && !definition.ContainsKey("KEY")) Add(result, action, value, slot);
            }
            AddNext(result, section);
            return new(section, result.ToImmutable());
        }
        foreach (var (action, values) in definitions.EnumerateSections())
            if (!action.StartsWith('-') && !values.ContainsKey("KEY"))
            for (int slot = 1; slot <= 2; slot++)
                if (values.TryGetValue($"PSW_{slot}", out string? value)) Add(result, action, value, slot);
        AddNext(result, fixedSessionId is uint character && character != 0
            ? BinaryPrimitives.ReverseEndianness(character).ToString(CultureInfo.InvariantCulture) : "PSW");
        return new("PSW", result.ToImmutable());
    }

    // Next actions: EnbNext's own saved file, otherwise the default chord when the retail map leaves it free.
    private static void AddNext(ImmutableArray<EnbRetailKeyBinding>.Builder result, string section)
    {
        var map = EnbNextControls.Load(section);
        if (map is not null)
            foreach (var (action, chords) in map)
                if (EnbNextControls.IsNext(action))
                    for (int slot = 1; slot <= Math.Min(2, chords.Length); slot++) Add(result, action, chords[slot - 1], slot);
        // A default for each Next action the saved file does not name (none saved yet, or added since).
        foreach (var definition in EnbNextControls.Definitions)
            if (map?.ContainsKey(definition.Action) != true && Parse(definition.Action, definition.PswPrimary, 1) is { } binding &&
                !result.Any(b => b.Code != 0 && b.Code == binding.Code && b.Modifiers == binding.Modifiers))
                result.Add(binding);
    }

    public static EnbRetailKeyBinding? Parse(string action, string value, int slot)
    {
        EnbKeyModifiers modifiers = EnbKeyModifiers.None;
        int i = 0;
        for (; i < value.Length; i++)
        {
            var modifier = value[i] switch { '@' => EnbKeyModifiers.Alt, '^' => EnbKeyModifiers.Control, '$' => EnbKeyModifiers.Shift, _ => EnbKeyModifiers.None };
            if (modifier == EnbKeyModifiers.None) break;
            modifiers |= modifier;
        }
        string key = value[i..].Trim();
        return key.Length > 0 ? new(action, key, modifiers) { Slot = slot } : null;
    }

    private static void Add(ImmutableArray<EnbRetailKeyBinding>.Builder result, string action, string value, int slot)
    { if (Parse(action, value, slot) is { } binding) result.Add(binding); }
}
