using System.Globalization;
using System.Text;
using EnbNext.Assets;

namespace EnbNext.Engine;

/// <summary>One piece of music the player can choose: one component of a retail theme (retail plays one component of
/// a theme each time it is queued), or a file of the player's own.</summary>
internal sealed record EnbMusicTrack(EnbRetailMusicTheme Theme, string Name, string Group)
{
    public EnbRetailMusicComponent Component => Theme.Components[0];
    public bool Own => Theme.Id < 0;
    public bool Same(EnbRetailMusicTheme? theme) => theme is { Components.Count: > 0 } && theme.Id == Theme.Id &&
        string.Equals(theme.Components[0].ResourceName, Component.ResourceName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Next music player (Modern UI). Left alone, music is chosen and played as retail does
/// (0043A1F0: the character's race and Other themes, combat music while attacked). The player may pause it, skip to the
/// next or previous piece, choose any piece, move within it, keep combat from interrupting the music, choose the races
/// whose music plays (the character's own when none is chosen), and add music files of their own from a folder.
/// The window writes the requests here; the music audio carries them out on its next update and publishes what plays.
/// </summary>
internal sealed class EnbMusicPlayer
{
    public const int TerranRace = 0, JenquaiRace = 1, ProgenRace = 2, OtherRace = 8;
    public static readonly string[] RaceNames = ["Terran", "Jenquai", "Progen"];

    // The player's choices, kept between sessions.
    /// <summary>Combat music interrupts the music when you are attacked (retail). Off, the music plays on.</summary>
    public bool CombatMusic { get; set; } = true;
    /// <summary>The races whose music plays (bit 0 Terran, 1 Jenquai, 2 Progen); 0 is the character's own race (retail).
    /// Music for everyone (Other) always plays, as in retail.</summary>
    public int Races { get; set; }
    /// <summary>The folder with the player's own music (mp3 or wav; Combat and Planet subfolders for those moments).</summary>
    public string Folder { get; set; } = DefaultFolder;
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "EnbNext");

    // Requests from the window; the music audio takes them on its next update.
    public bool Paused { get; set; }
    internal int Step;
    internal EnbMusicTrack? Chosen;
    internal double? Seek;
    internal bool RescanRequested = true;
    public void Next() { Step = 1; Paused = false; }
    public void Previous() { Step = -1; Paused = false; }
    public void Play(EnbMusicTrack track) { Chosen = track; Step = 0; Paused = false; }
    public void SeekTo(double seconds) => Seek = Math.Max(0, seconds);
    public void Rescan() => RescanRequested = true;

    // What plays, as the music audio last saw it.
    public EnbMusicTrack? Current { get; internal set; }
    public double Position { get; internal set; }
    public double Duration { get; internal set; }
    /// <summary>The kind of music now: Wander, Combat or Planet (empty while there is none).</summary>
    public string Category { get; internal set; } = "";
    /// <summary>The character's race (-1 until known).</summary>
    public int OwnRace { get; internal set; } = -1;
    /// <summary>Every piece there is: the game's music by race, the sector themes, then the player's own.</summary>
    public IReadOnlyList<EnbMusicTrack> Library { get; internal set; } = [];
    /// <summary>The files found in the folder at the last scan, or why it could not be read.</summary>
    public int OwnCount { get; internal set; }
    public string? FolderError { get; internal set; }

    /// <summary>Whether a race's music plays now.</summary>
    public bool RaceIncluded(int race) => race == OtherRace || (Races == 0 ? race == OwnRace : (Races & (1 << race)) != 0);
    /// <summary>Whether a race is chosen in the window (with none chosen, the character's own).</summary>
    public bool RaceChosen(int race) => Races == 0 ? race == OwnRace : (Races & (1 << race)) != 0;
    /// <summary>Turns a race's music on or off. The last race cannot be turned off (music for everyone stays).</summary>
    public void ToggleRace(int race)
    {
        if (race is < 0 or > 2) return;
        int chosen = Races == 0 ? (OwnRace is >= 0 and <= 2 ? 1 << OwnRace : 0) : Races;
        int next = chosen ^ (1 << race);
        if (next == 0) return;
        // Exactly the character's own race again is retail's choice.
        Races = OwnRace is >= 0 and <= 2 && next == 1 << OwnRace ? 0 : next;
    }
    /// <summary>Whether a piece is one the music may choose now (its kind and race; sector themes only by choice).</summary>
    public bool InPlaylist(EnbMusicTrack track) =>
        track.Theme.Sector == -1 && RaceIncluded(track.Theme.Race) && track.Theme.Type.Equals(Category, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every component of every theme as a piece, then the player's own files.</summary>
    internal static IReadOnlyList<EnbMusicTrack> BuildLibrary(EnbRetailMusicCatalog catalog, IReadOnlyList<EnbRetailMusicTheme> own)
    {
        var tracks = new List<EnbMusicTrack>();
        foreach (var theme in catalog.Themes)
            for (int i = 0; i < theme.Components.Count; i++)
            {
                var component = theme.Components[i];
                // null.wav and other placeholders are silence, not music.
                if (!component.ResourceName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) continue;
                string group = theme.Race switch
                {
                    TerranRace => "Terran", JenquaiRace => "Jenquai", ProgenRace => "Progen",
                    _ => theme.Sector != -1 ? "Sector themes" : "Everyone",
                };
                tracks.Add(new(theme with { Components = [component] }, PieceName(component.ResourceName), group));
            }
        foreach (var theme in own) tracks.Add(new(theme, Path.GetFileNameWithoutExtension(theme.Components[0].ResourceName), "Your music"));
        return tracks.AsReadOnly();
    }

    /// <summary>A retail music file's name as words: "TerranWander01-2.mp3" reads "Terran Wander 1, part 2".</summary>
    internal static string PieceName(string resource)
    {
        string stem = Path.GetFileNameWithoutExtension(resource);
        string part = "";
        int dash = stem.LastIndexOf('-');
        if (dash > 0 && int.TryParse(stem.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int piece))
        { part = ", part " + piece.ToString(CultureInfo.InvariantCulture); stem = stem[..dash]; }
        var words = new StringBuilder();
        for (int i = 0; i < stem.Length; i++)
        {
            char c = stem[i];
            if (c is '_' or ' ') { if (words.Length > 0 && words[^1] != ' ') words.Append(' '); continue; }
            bool boundary = i > 0 && (char.IsUpper(c) && !char.IsUpper(stem[i - 1]) || char.IsDigit(c) && !char.IsDigit(stem[i - 1]));
            if (boundary && words.Length > 0 && words[^1] != ' ') words.Append(' ');
            words.Append(c);
        }
        // "01" reads "1".
        string text = string.Join(' ', words.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.All(char.IsDigit) ? int.Parse(w, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : w));
        return text + part;
    }

    /// <summary>The player's own music: mp3 and wav files in the folder (Wander), and in its Combat and Planet
    /// subfolders. Each is a theme of one piece for everyone, in the order of its name.</summary>
    internal static (IReadOnlyList<EnbRetailMusicTheme> Themes, string? Error) LoadFolder(string folder)
    {
        var themes = new List<EnbRetailMusicTheme>();
        try
        {
            if (!EnbNext.Installation.EnbPathCase.DirectoryExists(folder)) return (themes, null);
            void Add(string directory, string type)
            {
                if (!EnbNext.Installation.EnbPathCase.DirectoryExists(directory)) return;
                foreach (string file in EnbNext.Installation.EnbPathCase.EnumerateFiles(directory).Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                             f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
                    themes.Add(new(-1 - themes.Count, OtherRace, type, 0, -1, 0, [new(Path.GetFullPath(file), 2, 2)]));
            }
            Add(folder, "Wander");
            Add(Path.Combine(folder, "Combat"), "Combat");
            Add(Path.Combine(folder, "Planet"), "Planet");
            return (themes, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return (themes, ex.Message); }
    }
}
