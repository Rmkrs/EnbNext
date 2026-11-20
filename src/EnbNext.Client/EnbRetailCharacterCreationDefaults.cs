using System.Buffers.Binary;
using System.Globalization;
using EnbNext.Assets;
using EnbNext.Net7.Protocol;

namespace EnbNext.Client;

/// <summary>Current retail 004BEBF0/004C0310/004C05D0 and 004F5FB0/004F6230/004F6330.</summary>
public static class EnbRetailCharacterCreationDefaults
{
    // 004C1B80..004C2240: authored editor UVs, with (.5,.5) when absent.
    public static (float U,float V) AvatarPaletteUv(EnbRetailIniDocument definitions,uint race,uint profession,uint gender,int part)
    {
        string section=$"{(gender==0?"male":"female")}_{new[]{"terran","jenquai","progen"}[race]}_{new[]{"warrior","tradesman","explorer"}[profession]}";
        string key=new[]{"HairOffset","EyeOffset","SkinOffset","ShirtPrimaryOffset","ShirtSecondaryOffset","PantsPrimaryOffset","PantsSecondaryOffset"}[part];
        if(!definitions.TryGetSection(section,out var values)||!values.TryGetValue(key,out var text))return(.5f,.5f);
        var components=text.Split(',');
        return(float.Parse(components[0],CultureInfo.InvariantCulture),float.Parse(components[1],CultureInfo.InvariantCulture));
    }

    public static Net7AvatarData Avatar(EnbRetailIniDocument definitions, uint race, uint profession, uint gender)
    {
        if (race > 2 || profession > 2 || gender > 1) throw new ArgumentOutOfRangeException(nameof(race));
        string section = $"{(gender == 0 ? "male" : "female")}_{new[] { "terran", "jenquai", "progen" }[race]}_{new[] { "warrior", "tradesman", "explorer" }[profession]}";
        if (!definitions.TryGetSection(section, out var values)) throw new InvalidDataException("Missing retail avatar defaults: " + section);
        byte Number(string key) => byte.Parse(values.GetValueOrDefault(key, "0"), CultureInfo.InvariantCulture);
        float[] Vector(string key, float[]? fallback = null) => values.TryGetValue(key, out var text)
            ? text.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray() : fallback ?? [0, 0, 0];
        uint Metal(string key) => unchecked((uint)int.Parse(values.GetValueOrDefault(key, "-1"), CultureInfo.InvariantCulture));
        uint[] Weights(string key) => Enumerable.Range(0, 5).Select(i => BitConverter.SingleToUInt32Bits(
            float.Parse(values.GetValueOrDefault(key + i, i == 0 ? "1" : "0"), CultureInfo.InvariantCulture))).ToArray();
        return new("player", "", 0, 0, 2, race, profession, gender, 0,
            Number("Personality"), Number("NLP"), Number("BodyType"), Number("PantsType"), Number("HeadType"),
            Number("HairNum"), Number("EarNum"), Number("GoggleNum"), Number("BeardNum"), 0, 0, 0,
            Number("HeadTextureNum"), Number("TattooTexture"), Vector("TattooOffset", [0, 0, 1]),
            Vector("HairColor"), Vector("HairColor"), Vector("EyeColor"), Vector("SkinColor"),
            Vector("ShirtPrimaryColor"), Vector("ShirtSecondaryColor"), Vector("PantsPrimaryColor"), Vector("PantsSecondaryColor"),
            Metal("ShirtPrimaryMetal"), Metal("ShirtSecondaryMetal"), Metal("PantsPrimaryMetal"), Metal("PantsSecondaryMetal"),
            values.GetValueOrDefault("Skeleton", ""), Weights("BodyWeight"), Weights("HeadWeight"));
    }

    /// <summary>The profession-preview pair receives additional head weights at retail 0067BF20.</summary>
    public static Net7AvatarData ClassPreviewAvatar(EnbRetailIniDocument definitions, uint race, uint profession, uint gender)
    {
        var avatar = Avatar(definitions, race, profession, gender);
        var weights = (uint[])avatar.HeadWeight.Clone();
        weights[0] = 0x3f000000; // 0.5
        if (gender == 0)
        {
            weights[2] = 0x3e800000; // 0.25
            weights[3] = 0x3e800000;
            weights[4] = 0x3e800000;
        }
        else
        {
            weights[1] = 0x3e800000;
        }
        return avatar with { HeadWeight = weights };
    }

    public static Net7ShipAppearanceData Ship(uint race, uint profession)
    {
        if (race > 2 || profession > 2) throw new ArgumentOutOfRangeException(nameof(race));
        byte[] bytes = new byte[Net7ShipAppearanceData.SerializedSize];
        void Word(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        Word(0, race); Word(4, profession);
        uint style = new uint[] { 1, 0, 2 }[profession]; // 00B75D28 / 00B75D4C.
        Word(8, style); Word(12, style); Word(16, 0x07ffffff);
        // Blank 26-byte ship name; CharacterViewCtor initializes white name text.
        for (int i = 0; i < 3; i++) Word(46 + 4 * i, 0x3f800000);
        // Racial color pairs are binary-authored, not machine-specific INI values.
        // 004F5FB0 constructs six entries; 004F6230 repeats the pair for four parts.
        uint[][][] colors = [
            [[0, 0, 0, 0], [0x3d6147ae, 0x3e449ba6, 0x3ef126e9, uint.MaxValue]],
            [[0, 0x3e851eb8, 0x3db020c5, uint.MaxValue], [0, 0, 0, 0]],
            [[0x3ef0a3d7, 0x3ccccccd, 0x3ccccccd, uint.MaxValue], [0, 0, 0, 0]]
        ];
        for (int i = 0; i < 8; i++)
        {
            var color = colors[race][i % 2];
            int offset = 58 + i * 17;
            for (int channel = 0; channel < 3; channel++) Word(offset + channel * 4, color[channel]);
            bytes[offset + 12] = 0;
            Word(offset + 13, color[3]);
        }
        return new(bytes);
    }

    /// <summary>
    /// Retail 004F5FB0 stores palette UVs alongside each racial paint default;
    /// 004F6230 copies the same channel pair to all four parts. The 194-byte
    /// appearance packet has no UV fields, so the editor retains them separately.
    /// </summary>
    public static (float U, float V) ShipPaintPaletteUv(uint race, int channel)
    {
        if (race > 2 || (uint)channel > 1) throw new ArgumentOutOfRangeException(nameof(race));
        (uint U, uint V) bits = (race, channel) switch
        {
            (0, 1) => (0x3ee147ae, 0x3f0dd2f2),
            (1, 0) => (0x3e1db22d, 0x3e77ced9),
            (2, 0) => (0x3f13b646, 0x3f06a7f0),
            _ => (0x3f000000, 0x3f000000)
        };
        return (BitConverter.UInt32BitsToSingle(bits.U), BitConverter.UInt32BitsToSingle(bits.V));
    }
}
