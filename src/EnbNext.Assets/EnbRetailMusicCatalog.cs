using System.Globalization;

namespace EnbNext.Assets;

public sealed record EnbRetailMusicComponent(string ResourceName, double FadeInSeconds, double FadeOutSeconds);
public sealed record EnbRetailMusicTheme(int Id, int Race, string Type, int LevelAvailable, int Sector,
    int LoopCount, IReadOnlyList<EnbRetailMusicComponent> Components);

/// <summary>Current retail 0043A1F0/0043B1C0. Data only; never starts audio or changes gameplay.</summary>
public sealed record EnbRetailMusicCatalog(IReadOnlyList<EnbRetailMusicTheme> Themes,
    double SilenceMinSeconds, double SilenceMaxSeconds)
{
    public double CombatFadeInSeconds { get; init; } = 6;
    public double CombatFadeOutSeconds { get; init; } = 2;
    public double WanderFadeInSeconds { get; init; } = 6;
    public double WanderFadeOutSeconds { get; init; } = 2;
    public double PlanetFadeInSeconds { get; init; } = 6;
    public double PlanetFadeOutSeconds { get; init; } = 2;
    public static EnbRetailMusicCatalog Load(string path) => Parse(EnbRetailIniDocument.Load(path));

    public static EnbRetailMusicCatalog Parse(EnbRetailIniDocument ini)
    {
        var themes = new List<EnbRetailMusicTheme>();
        foreach (var (section, values) in ini.EnumerateSections())
        {
            if (!section.StartsWith("MusicTheme-", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(section.AsSpan(11), out int id)) continue;
            int count = Integer(values, "ComponentCount", 0);
            if (count < 0 || count > 1024) throw new InvalidDataException($"Invalid component count in {section}.");
            var components = new List<EnbRetailMusicComponent>();
            for (int i = 0; i < count; i++)
            {
                string name = Text(values, $"Component-{i:00}", "None");
                if (name.Equals("None", StringComparison.OrdinalIgnoreCase) || name.Length == 0)
                    throw new InvalidDataException($"Missing component {i} in {section}.");
                components.Add(new(name, Seconds(values, $"FadeIn-{i:00}"), Seconds(values, $"FadeOut-{i:00}")));
            }
            int race = Text(values, "Race", "Other").ToUpperInvariant() switch
                { "TERRAN" => 0, "JENQUAI" => 1, "PROGEN" => 2, _ => 8 };
            themes.Add(new(id, race, Text(values, "Type", "Wander"), Integer(values, "LevelAvailable", 0),
                Integer(values, "Sector", -1), Integer(values, "LoopCount", 0), components.AsReadOnly()));
        }
        double min = 0, max = 0;
        if (ini.TryGetSection("Global", out var global))
        {
            min = Seconds(global, "SilenceMin"); max = Seconds(global, "SilenceMax");
        }
        if (max < min) throw new InvalidDataException("Music SilenceMax is less than SilenceMin.");
        double Fade(string key,double fallback) => ini.TryGetSection("Global",out var values) && values.ContainsKey(key)
            ? Seconds(values,key) : fallback;
        return new(themes.AsReadOnly(), min, max) {
            CombatFadeInSeconds=Fade("CombatFadeIn",6), CombatFadeOutSeconds=Fade("CombatFadeOut",2),
            WanderFadeInSeconds=Fade("WanderFadeIn",6), WanderFadeOutSeconds=Fade("WanderFadeOut",2),
            PlanetFadeInSeconds=Fade("PlanetFadeIn",6), PlanetFadeOutSeconds=Fade("PlanetFadeOut",2) };
    }

    public IReadOnlyList<EnbRetailMusicTheme> Playlist(string category,int race,int level,int? previousId,Random random)
        => Playlist(category,race,level,previousId,(low,high)=>random.Next(low,high+1));

    public IReadOnlyList<EnbRetailMusicTheme> Playlist(string category,int race,int level,int? previousId,Func<int,int,int> random)
        => Playlist(category,r => r == race || r == 8,level,previousId,random);

    /// <summary>The same queue for the races a Next music player chose (retail: the character's race and Other).</summary>
    public IReadOnlyList<EnbRetailMusicTheme> Playlist(string category,Func<int,bool> race,int level,int? previousId,Func<int,int,int> random)
    {
        var eligible = Themes.Where(t => race(t.Race) &&
            t.Type.Equals(category, StringComparison.OrdinalIgnoreCase) && t.LevelAvailable <= level &&
            t.Sector == -1 && t.Components.Count > 0).ToList();
        if (eligible.Count > 1 && previousId.HasValue) eligible.RemoveAll(t => t.Id == previousId);
        var result = new List<EnbRetailMusicTheme>();
        while (eligible.Count > 0)
        {
            int index = random(0,eligible.Count-1); result.Add(SelectComponent(eligible[index],random)); eligible.RemoveAt(index);
        }
        return result.AsReadOnly();
    }

    // 00437920 selects one component while each theme is queued, between
    // permutation draws, not when decoding starts or after the whole shuffle.
    public static EnbRetailMusicTheme SelectComponent(EnbRetailMusicTheme theme,Func<int,int,int> random)
        => theme with { Components=[theme.Components[random(0,theme.Components.Count-1)]] };

    private static string Text(IReadOnlyDictionary<string,string> values, string key, string fallback)
        => values.TryGetValue(key, out var value) ? value.Trim() : fallback;
    private static int Integer(IReadOnlyDictionary<string,string> values, string key, int fallback)
        => int.TryParse(Text(values,key,""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
    private static double Seconds(IReadOnlyDictionary<string,string> values, string key)
    {
        string text = Text(values,key,"0");
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            !double.IsFinite(value) || value < 0 || value > 86400)
            throw new InvalidDataException($"Invalid music timing {key}: {text}");
        return value;
    }
}
