using System.Collections.Immutable;
using System.Text.Json;

namespace EnbNext.Assets;

/// <summary>
/// Next key actions shown in the retail Control Options lists. Their bindings are kept in EnbNext's own
/// files (Directory/next-keys/section.json); the retail control_keys.ini and keymap.ini are never changed.
/// A default is used only when nothing is saved and the chord is free in the retail map.
/// </summary>
public static class EnbNextControls
{
    public const string HideInterface = "Next Hide Interface";
    public const string Factions = "Next Factions", Local = "Next Local", Items = "Next Item Catalogue", Music = "Next Music", CaptainLog = "Next Captain's Log";
    /// <summary>Modern UI windows that have no retail key.</summary>
    public static readonly ImmutableArray<string> ModernWindows = [Factions, Local, Items, Music, CaptainLog];
    /// <summary>The Modern UI action bar's second row (shown as Alt+1-6): a key of its own per slot instead of
    /// retail's Alt bank switch.</summary>
    public static readonly ImmutableArray<string> ModernSlots = ["Use Slot 7", "Use Slot 8", "Use Slot 9", "Use Slot 10", "Use Slot 11", "Use Slot 12"];
    /// <summary>Retail's Alt bank switch: the Modern UI bar shows both banks, so it has no use there.</summary>
    public const string ShiftShortcuts = "Shift Shortcuts";
    /// <summary>A control that only Modern UI uses: listed and active only with Modern UI on.</summary>
    public static bool ModernOnly(string action) => ModernWindows.Contains(action) || ModernSlots.Contains(action);
    /// <summary>A control that only the classic interface uses: listed and active only with Modern UI off.</summary>
    public static bool ClassicOnly(string action) => action == ShiftShortcuts;
    public static bool Shown(string action, bool modern) => modern ? !ClassicOnly(action) : !ModernOnly(action);
    public static readonly ImmutableArray<EnbRetailControlDefinition> Definitions =
    [
        new(HideInterface, "Hide user interface", "Camera", null, "@Z", "", "@Z", ""),
        .. ModernSlots.Select((action, i) => new EnbRetailControlDefinition(action, $"Fire/Activate slot {i + 7}", "Activation", null, $"@{i + 1}", "", $"@{i + 1}", "")),
        new(Factions, "Toggle factions on/off", "General", null, "", "", "", ""),
        new(Local, "Toggle Local on/off", "General", null, "B", "", "B", ""),
        new(Items, "Toggle item catalogue on/off", "General", null, "", "", "", ""),
        new(Music, "Toggle music player on/off", "General", null, "", "", "", ""),
        new(CaptainLog, "Toggle Captain's Log on/off", "General", null, "", "", "", ""),
    ];
    /// <summary>The Next preferences directory, set by the client at start-up.</summary>
    public static string? Directory { get; set; }
    public static bool IsNext(string action) => Definitions.Any(d => d.Action.Equals(action, StringComparison.OrdinalIgnoreCase));
    private static string? PathOf(string section) => Directory is { } directory ? System.IO.Path.Combine(directory, "next-keys", section + ".json") : null;

    /// <summary>Saved chords per action ([primary, alternate]); null when this section has none saved.</summary>
    public static Dictionary<string, string[]>? Load(string section)
    {
        try
        {
            return PathOf(section) is { } path && EnbNext.Installation.EnbPathCase.FileExists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(path))) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static void Save(string section, Func<string, int, string> encoded)
    {
        if (PathOf(section) is not { } path) return;
        var saved = Definitions.ToDictionary(d => d.Action, d => new[] { encoded(d.Action, 1), encoded(d.Action, 2) });
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(saved));
    }
}
