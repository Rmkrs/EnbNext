using System.Collections.Immutable;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public static class EnbRetailAvatarDefaults
{
    // Current client 004C0310 initializes the record; 004C05D0 overlays avatardef.ini.
    // This presentation-only narrator never supplies a character-creation packet.
    public static EnbSceneAvatarSnapshot Narrator(EnbRetailAssetBundle assets) => Named(assets, "narrator", 1);
    public static EnbSceneAvatarSnapshot Help(EnbRetailAssetBundle assets) => Named(assets, "Megan", 2);

    private static EnbSceneAvatarSnapshot Named(EnbRetailAssetBundle assets, string section, uint type)
    {
        var ini = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.CommonDataDirectory, "avatardef.ini"));
        if (!ini.TryGetSection(section, out var values)) throw new InvalidDataException("Missing avatar definition: " + section);
        byte Number(string key) => byte.Parse(values.GetValueOrDefault(key, "0"), CultureInfo.InvariantCulture);
        ImmutableArray<float> Color(string key) => values.TryGetValue(key, out var value)
            ? value.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToImmutableArray() : [0, 0, 0];
        return new("", "", type, 0, 2, 0, 0, Number("Gender"), 0, Number("Personality"), Number("NLP"),
            Number("BodyType"), Number("PantsType"), Number("HeadType"), Number("HairNum"), Number("EarNum"),
            Number("GoggleNum"), Number("BeardNum"), 0, 0, 0, Number("HeadTextureNum"), Number("TattooTexture"),
            [0, 0, 1], Color("HairColor"), Color("HairColor"), Color("EyeColor"), Color("SkinColor"),
            Color("ShirtPrimaryColor"), Color("ShirtSecondaryColor"), Color("PantsPrimaryColor"), Color("PantsSecondaryColor"),
            uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, values.GetValueOrDefault("Skeleton", ""),
            [0x3f800000, 0, 0, 0, 0], [0x3f800000, 0, 0, 0, 0]);
    }
}
