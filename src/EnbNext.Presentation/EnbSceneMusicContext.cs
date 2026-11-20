using System.Buffers.Binary;
using EnbNext.Net7.World;

namespace EnbNext.Presentation;

public sealed record EnbSceneMusicContext(int Race, int OverallLevel, int MusicId)
{
    public bool Combat { get; init; }
    public bool Planet { get; init; }
    public string Category => Combat ? "Combat" : Planet ? "Planet" : "Wander";
    public static EnbSceneMusicContext? From(Net7AuxDataSchema player, bool combat=false, bool planet=false)
    {
        // Retained local-player fields; never derive race from ship artwork.
        if (player.Resolve(2571).State == 0) return null;
        object? raw = player.Resolve(2571).Value;
        if (raw is not byte[] { Length: 4 } raceBytes) return null;
        // 0043A1F0 loads the themes whose Race code equals the avatar race or is Other (8);
        // any other race value simply matches Other themes only.
        int race = BinaryPrimitives.ReadInt32LittleEndian(raceBytes);
        int Level(int id) => player.Resolve(id).Value is int value ? Math.Max(0,value) : 0;
        return new(race, Level(4450) + Level(4452) + Level(4454),
            player.Resolve(4460).Value is int musicId ? musicId : -1) {Combat=combat,Planet=planet};
    }
}
