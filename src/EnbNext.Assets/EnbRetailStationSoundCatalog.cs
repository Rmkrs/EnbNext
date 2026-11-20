namespace EnbNext.Assets;

public sealed record EnbStationAmbientEntry(string Sound, bool OnEntry,
    int MinDelayMilliseconds, int MaxDelayMilliseconds, float MinPan, float MaxPan, bool FixedPosition)
{
    public EnbStationSoundBinding? Binding { get; init; }
}

public sealed record EnbStationSoundBinding(string ObjectKey, string Bone, bool Follow);
public sealed record EnbStationTrackedSound(string Mesh, string Bone, string Sound, bool OnEntry,
    bool OnlyIfBoneUsed, int MinDelayMilliseconds, int MaxDelayMilliseconds);

public sealed record EnbStationAmbientRoom(string Section, IReadOnlyList<EnbStationAmbientEntry> Entries)
{
    public IReadOnlyList<EnbStationTrackedSound> Tracked { get; init; } = [];
}

/// <summary>Current client 00441C40/00441DD0. Installed starbasesounds{ID}.ini owns room audio.</summary>
public sealed class EnbRetailStationSoundCatalog(EnbRetailIniDocument document)
{
    private static readonly string[] RoomSections = ["Hangar", "MainHall", "Bazaar", "NPCLounge", "PlayerLounge"];

    public EnbStationAmbientRoom Room(uint type, string model)
    {
        string section = document.TryGetSection(model, out _) ? model :
            type < RoomSections.Length ? RoomSections[type] : "";
        if (!document.TryGetSection(section, out var values)) return new(section, []);
        if (!document.TryGetSection("General", out var general)) general = new Dictionary<string, string>();
        float minDelay = Number(general, "DefaultMinDelay", 10), maxDelay = Number(general, "DefaultMaxDelay", 20);
        float minPan = Number(general, "DefaultMinPan", .5f), maxPan = Number(general, "DefaultMaxPan", .5f);
        var entries = new List<EnbStationAmbientEntry>();
        var tracked = new List<EnbStationTrackedSound>();
        // Retail probes every slot, including gaps; it does not stop at the first missing key.
        for (int i = 1; i <= 64; i++)
        {
            AddTracked(i, false);
            AddTracked(i, true);
            if (values.TryGetValue($"LoopingAmbient{i}", out var loop) && !string.IsNullOrWhiteSpace(loop))
                entries.Add(new(loop, true, 0, 0, .5f, .5f, false));
            if (values.TryGetValue($"NonLoopingAmbient{i}", out var sound) && !string.IsNullOrWhiteSpace(sound))
            {
                int min = Milliseconds(Number(values, $"MinDelay{i}", minDelay));
                int max = Milliseconds(Number(values, $"MaxDelay{i}", maxDelay));
                float left = Number(values, $"MinPan{i}", minPan), right = Number(values, $"MaxPan{i}", maxPan);
                entries.Add(new(sound, false, min, max, Math.Min(left, right), Math.Max(left, right),
                    !values.ContainsKey($"FixedPosition{i}") || EnbRetailIniDocument.ReadBool(values, $"FixedPosition{i}")));
            }
        }
        return new(section, entries) { Tracked = tracked };

        void AddTracked(int slot, bool loop)
        {
            string prefix = loop ? "LoopingTracked" : "Tracked";
            if (!values.TryGetValue($"{prefix}Mesh{slot}", out var mesh) ||
                !values.TryGetValue($"{prefix}Sound{slot}", out var sound)) return;
            string bone = values.GetValueOrDefault($"{prefix}Bone{slot}", "ROOTTRANSFORM");
            string guard = loop ? "OnlyAddLoopingSoundIfBoneUsed" : "OnlyAddSoundIfBoneUsed";
            tracked.Add(new(mesh, bone, sound, loop, EnbRetailIniDocument.ReadBool(values, guard + slot),
                loop ? 0 : Milliseconds(Number(values, $"TrackedMinDelay{slot}", minDelay)),
                loop ? 0 : Milliseconds(Number(values, $"TrackedMaxDelay{slot}", maxDelay))));
        }
    }

    private static float Number(IReadOnlyDictionary<string, string> values, string key, float fallback)
        => EnbRetailIniDocument.TryReadFloat(values, key, out var value) && float.IsFinite(value) ? value : fallback;
    private static int Milliseconds(float seconds)
        => seconds is >= 0 and < 2147483 ? (int)(seconds * 1000f) : throw new InvalidDataException("Invalid station ambient delay.");
}
