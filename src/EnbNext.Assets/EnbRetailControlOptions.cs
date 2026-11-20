using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;

namespace EnbNext.Assets;

public sealed record EnbRetailControlDefinition(string Action, string Text, string Context, string? FixedKey,
    string PswPrimary, string PswAlternate, string FpsPrimary, string FpsAlternate);

/// <summary>Installed control_keys.ini order and a local editor draft. Current client 005CB000 / 0071A860.</summary>
public sealed class EnbRetailControlOptions
{
    public ImmutableArray<EnbRetailControlDefinition> Definitions { get; }
    public EnbRetailKeyBindings Draft { get; private set; }
    public EnbRetailControlOptions(EnbRetailIniDocument definitions, EnbRetailKeyBindings current)
    {
        var retail = definitions.EnumerateSections().Where(s => !s.Key.StartsWith('-') && s.Value.ContainsKey("Context"))
            .Select(s => new EnbRetailControlDefinition(s.Key, s.Value.GetValueOrDefault("Text", s.Key),
                s.Value["Context"], s.Value.GetValueOrDefault("KEY"),
                s.Value.GetValueOrDefault("PSW_1", ""), s.Value.GetValueOrDefault("PSW_2", ""),
                s.Value.GetValueOrDefault("SIM_1", ""), s.Value.GetValueOrDefault("SIM_2", ""))).ToList();
        // Next rows follow the last editable row of their category, before its fixed (KEY) rows.
        foreach (var next in EnbNextControls.Definitions)
        {
            int at = retail.FindLastIndex(d => d.Context == next.Context && d.FixedKey is null);
            retail.Insert(at < 0 ? retail.Count : at + 1, next);
        }
        Definitions = [.. retail];
        Draft = current;
    }
    public EnbRetailKeyBinding? Binding(string action, int slot) =>
        Draft.Bindings.FirstOrDefault(b => b.Action.Equals(action, StringComparison.OrdinalIgnoreCase) && b.Slot == slot);

    public void Default(bool fps)
    {
        var bindings = ImmutableArray.CreateBuilder<EnbRetailKeyBinding>();
        foreach (var d in Definitions.Where(d => d.FixedKey is null))
            foreach (int slot in new[] { 1, 2 })
                if (EnbRetailKeyBindings.Parse(d.Action, (fps, slot) switch
                    { (true, 1) => d.FpsPrimary, (true, 2) => d.FpsAlternate, (false, 1) => d.PswPrimary, _ => d.PswAlternate }, slot) is { } b)
                    bindings.Add(b);
        Draft = Draft with { Bindings = bindings.ToImmutable() };
    }

    public EnbRetailKeyBinding? Conflict(string action, EnbRetailKeyBinding candidate) => Draft.Bindings.FirstOrDefault(b =>
        !b.Action.Equals(action, StringComparison.OrdinalIgnoreCase) && SameChord(b, candidate));

    public void Assign(string action, int slot, EnbRetailKeyBinding? candidate)
    {
        if (slot is < 1 or > 2 || !Definitions.Any(d => d.Action == action && d.FixedKey is null))
            throw new ArgumentException("Not an editable control slot.");
        // 0071A860 clears duplicate chords in BOTH columns, including the same action.
        var bindings = Draft.Bindings.Where(b => !(b.Action == action && b.Slot == slot) &&
            (candidate is null || !SameChord(b, candidate))).ToImmutableArray();
        if (candidate is not null) bindings = bindings.Add(candidate with { Action = action, Slot = slot });
        Draft = Draft with { Bindings = bindings };
    }
    private static bool SameChord(EnbRetailKeyBinding a, EnbRetailKeyBinding b) =>
        a.Code != 0 && a.Code == b.Code && a.Modifiers == b.Modifiers;

    public static string Section(uint session) => BinaryPrimitives.ReverseEndianness(session).ToString(CultureInfo.InvariantCulture);

    /// <summary>0071C170 replaces this character's section and selects it as Default/User.</summary>
    public string SaveText(string text, uint session)
    {
        string section = Section(session);
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (!lines[i].Trim().Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase)) continue;
            int end = i + 1;
            while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
            lines.RemoveRange(i, end - i);
        }
        int defaults = lines.FindIndex(l => l.Trim().Equals("[Default]", StringComparison.OrdinalIgnoreCase));
        if (defaults < 0) { lines.Add("[Default]"); defaults = lines.Count - 1; }
        int next = defaults + 1;
        while (next < lines.Count && !lines[next].TrimStart().StartsWith('[')) next++;
        for (int i = next - 1; i > defaults; i--)
            if (lines[i].Split('=')[0].Trim().Equals("User", StringComparison.OrdinalIgnoreCase)) lines.RemoveAt(i);
        lines.Insert(defaults + 1, "User=" + section);
        lines.AddRange(["", "[" + section + "]"]);
        foreach (var d in Definitions.Where(d => d.FixedKey is null && !EnbNextControls.IsNext(d.Action)))
            foreach (int slot in new[] { 1, 2 }) lines.Add($"{slot}_{d.Action}={Binding(d.Action, slot)?.Encoded ?? ""}");
        return string.Join(text.Contains("\r\n") ? "\r\n" : "\n", lines) + "\n";
    }
}
