namespace EnbNext.Assets;

/// <summary>
/// Logical names for presentation audio shipped by EnbNext rather than read
/// from the selected retail installation.
/// </summary>
public static class EnbNextAudioAssetCatalog
{
    public const string Prefix = "enbnext:audio/";

    private static readonly IReadOnlyDictionary<string, string> Resources =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [CharacterCreation("terran-scout", 1)] = Path.Combine("Assets", "Audio", "CharacterCreation", "terran-scout-1.mp3"),
            [CharacterCreation("terran-scout", 2)] = Path.Combine("Assets", "Audio", "CharacterCreation", "terran-scout-2.mp3"),
            [CharacterCreation("terran-scout", 3)] = Path.Combine("Assets", "Audio", "CharacterCreation", "terran-scout-3.mp3"),
            [CharacterCreation("progen-privateer", 1)] = Path.Combine("Assets", "Audio", "CharacterCreation", "progen-privateer-1.mp3"),
            [CharacterCreation("progen-privateer", 2)] = Path.Combine("Assets", "Audio", "CharacterCreation", "progen-privateer-2.mp3"),
            [CharacterCreation("progen-privateer", 3)] = Path.Combine("Assets", "Audio", "CharacterCreation", "progen-privateer-3.mp3"),
            [CharacterCreation("jenquai-seeker", 1)] = Path.Combine("Assets", "Audio", "CharacterCreation", "jenquai-seeker-1.mp3"),
            [CharacterCreation("jenquai-seeker", 2)] = Path.Combine("Assets", "Audio", "CharacterCreation", "jenquai-seeker-2.mp3"),
            [CharacterCreation("jenquai-seeker", 3)] = Path.Combine("Assets", "Audio", "CharacterCreation", "jenquai-seeker-3.mp3"),
        };

    public static string CharacterCreation(string profession, int part)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profession);
        if (part is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(part));
        return $"{Prefix}character-creation/{profession}-{part}.mp3";
    }

    public static bool TryResolve(string logicalName, string applicationDirectory, out string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        if (Resources.TryGetValue(logicalName, out string? relativePath))
        {
            path = Path.Combine(applicationDirectory, relativePath);
            return true;
        }

        path = "";
        return false;
    }

    public static EnbRetailSoundDefinition CharacterCreationSpeechDefinition(
        string logicalName,
        EnbRetailSoundDefinition fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        if (!Resources.ContainsKey(logicalName))
            throw new ArgumentException("Logical name is not packaged EnbNext character-creation audio.",nameof(logicalName));

        // The six retail profession sequences explicitly author these values in
        // sounds.ini. Application-owned companions need the same playback
        // contract instead of the quieter generic SOUND_DEFAULTS volume.
        return fallback with {LoopCount=1,Priority=1f,Volume=.8f,SoundType=2};
    }
}
