using System.Buffers.Binary;
using System.Text;

namespace EnbNext.Net7.Protocol;

/// <summary>
/// Create context. Retail 006164E0 passes its Skip Beginner Tutorial toggle as
/// Control (0 plays the tutorial, 1 skips it). The wire scalar remains byte-valued.
/// </summary>
public sealed record Net7AvatarCreateRequest(
    uint TargetShardId,
    int TargetSlot,
    byte Control,
    Net7AvatarData Avatar,
    Net7ShipAppearanceData ShipAppearance);

/// <summary>
/// Exact current 0xC2 ShipAppearanceData byte object used by character creation.
/// Detailed historical field names are intentionally not invented here.
/// </summary>
public sealed class Net7ShipAppearanceData
{
    public const int SerializedSize = 0xC2;
    private readonly byte[] _bytes;

    public Net7ShipAppearanceData(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != SerializedSize)
        {
            throw new ArgumentException(
                $"ShipAppearanceData must be exactly 0x{SerializedSize:X} bytes; got 0x{bytes.Length:X}.",
                nameof(bytes));
        }

        _bytes = bytes.ToArray();
    }

    public ReadOnlyMemory<byte> Bytes => _bytes.ToArray();
    public byte[] ToArray() => _bytes.ToArray();

    public static Net7ShipAppearanceData Decode(ReadOnlySpan<byte> bytes) => new(bytes);
}

public sealed record Net7AvatarCreateResult(int TargetSlot, Net7AvatarRoster Roster);

/// <summary>
/// Delete has no server success boolean in the closed protocol. ServerRequestSent
/// is only a local emission fact; the returned roster is the authoritative result.
/// </summary>
public sealed record Net7AvatarDeleteResult(int TargetSlot, bool ServerRequestSent, Net7AvatarRoster Roster);

/// <summary>Exact AvatarData producer used by the fixed create body.</summary>
public static class Net7AvatarCreateCodec
{
    public const int MaximumCreateAvatarDataSize = 0x10C;
    public const int MaximumCreateSkeletonLength = 0x1B;

    public static byte[] EncodeAvatarData(Net7AvatarData avatar)
        => EncodeAvatarData(avatar, true);

    /// <summary>AvatarData used by recustomization, preserving the roster skeleton string.</summary>
    public static byte[] EncodeRecustomizationData(Net7AvatarData avatar)
        => EncodeAvatarData(avatar, false);

    private static byte[] EncodeAvatarData(Net7AvatarData avatar, bool fixedCreate)
    {
        ArgumentNullException.ThrowIfNull(avatar);
        byte[] skeleton = GetLatin1Bytes(avatar.Skeleton, nameof(avatar.Skeleton));
        if (fixedCreate && skeleton.Length > MaximumCreateSkeletonLength)
        {
            throw new InvalidDataException(
                $"Skeleton exceeds the 27-byte capacity of the fixed proxy create envelope: {skeleton.Length} bytes.");
        }

        RequireLength(avatar.TattooOffset, 3, nameof(avatar.TattooOffset));
        RequireLength(avatar.HairColor, 3, nameof(avatar.HairColor));
        RequireLength(avatar.BeardColor, 3, nameof(avatar.BeardColor));
        RequireLength(avatar.EyeColor, 3, nameof(avatar.EyeColor));
        RequireLength(avatar.SkinColor, 3, nameof(avatar.SkinColor));
        RequireLength(avatar.ShirtPrimaryColor, 3, nameof(avatar.ShirtPrimaryColor));
        RequireLength(avatar.ShirtSecondaryColor, 3, nameof(avatar.ShirtSecondaryColor));
        RequireLength(avatar.PantsPrimaryColor, 3, nameof(avatar.PantsPrimaryColor));
        RequireLength(avatar.PantsSecondaryColor, 3, nameof(avatar.PantsSecondaryColor));
        RequireLength(avatar.BodyWeight, 5, nameof(avatar.BodyWeight));
        RequireLength(avatar.HeadWeight, 5, nameof(avatar.HeadWeight));

        byte[] payload = new byte[0xF1 + skeleton.Length];
        WriteFixedLatin1(payload.AsSpan(0x000, 0x14), avatar.FirstName, nameof(avatar.FirstName));
        WriteFixedLatin1(payload.AsSpan(0x014, 0x14), avatar.LastName, nameof(avatar.LastName));
        WriteUInt32(payload, 0x028, avatar.AvatarType);
        payload[0x02C] = avatar.UnknownU8;
        payload[0x02D] = avatar.AvatarVersion;
        WriteUInt32(payload, 0x02E, avatar.Race);
        WriteUInt32(payload, 0x032, avatar.Profession);
        WriteUInt32(payload, 0x036, avatar.Gender);
        WriteUInt32(payload, 0x03A, avatar.MoodType);
        payload[0x03E] = avatar.Personality;
        payload[0x03F] = avatar.Nlp;
        payload[0x040] = avatar.BodyType;
        payload[0x041] = avatar.PantsType;
        payload[0x042] = avatar.HeadType;
        payload[0x043] = avatar.HairNum;
        payload[0x044] = avatar.EarNum;
        payload[0x045] = avatar.GoggleNum;
        payload[0x046] = avatar.BeardNum;
        payload[0x047] = avatar.WeaponHipNum;
        payload[0x048] = avatar.WeaponUniqueNum;
        payload[0x049] = avatar.WeaponBackNum;
        payload[0x04A] = avatar.HeadTextureNum;
        payload[0x04B] = avatar.TattooTextureNum;
        WriteFloatArray(payload, 0x04C, avatar.TattooOffset);
        WriteFloatArray(payload, 0x058, avatar.HairColor);
        WriteFloatArray(payload, 0x064, avatar.BeardColor);
        WriteFloatArray(payload, 0x070, avatar.EyeColor);
        WriteFloatArray(payload, 0x07C, avatar.SkinColor);
        WriteFloatArray(payload, 0x088, avatar.ShirtPrimaryColor);
        WriteFloatArray(payload, 0x094, avatar.ShirtSecondaryColor);
        WriteFloatArray(payload, 0x0A0, avatar.PantsPrimaryColor);
        WriteFloatArray(payload, 0x0AC, avatar.PantsSecondaryColor);
        WriteUInt32(payload, 0x0B8, avatar.ShirtPrimaryMetal);
        WriteUInt32(payload, 0x0BC, avatar.ShirtSecondaryMetal);
        WriteUInt32(payload, 0x0C0, avatar.PantsPrimaryMetal);
        WriteUInt32(payload, 0x0C4, avatar.PantsSecondaryMetal);

        skeleton.CopyTo(payload, 0x0C8);
        payload[0x0C8 + skeleton.Length] = 0;
        int bodyWeightOffset = 0x0C8 + skeleton.Length + 1;
        WriteUInt32Array(payload, bodyWeightOffset, avatar.BodyWeight);
        WriteUInt32Array(payload, bodyWeightOffset + 0x14, avatar.HeadWeight);

        if (bodyWeightOffset + 0x28 != payload.Length)
            throw new InvalidOperationException("AvatarData geometry did not end at the record boundary.");

        return payload;
    }

    private static void WriteFixedLatin1(Span<byte> destination, string value, string parameterName)
    {
        byte[] bytes = GetLatin1Bytes(value, parameterName);
        int copy = Math.Min(bytes.Length, destination.Length);
        bytes.AsSpan(0, copy).CopyTo(destination);
        // destination is zero-initialized by the caller. Current retail serializes
        // the fixed object bytes verbatim, so a full-width 20-byte name is allowed.
    }

    private static byte[] GetLatin1Bytes(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.IndexOf('\0') >= 0)
            throw new ArgumentException("Embedded NUL is not representable in current AvatarData strings.", parameterName);
        foreach (char c in value)
        {
            if (c > 0x00FF)
                throw new ArgumentException($"Character U+{(int)c:X4} is outside the byte-preserving Latin-1 boundary.", parameterName);
        }
        return Encoding.Latin1.GetBytes(value);
    }

    private static void RequireLength(Array values, int expected, string name)
    {
        ArgumentNullException.ThrowIfNull(values, name);
        if (values.Length != expected)
            throw new ArgumentException($"{name} must contain exactly {expected} values; got {values.Length}.", name);
    }

    private static void WriteUInt32(byte[] destination, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(offset, 4), value);

    private static void WriteUInt32Array(byte[] destination, int offset, uint[] values)
    {
        for (int i = 0; i < values.Length; i++)
            WriteUInt32(destination, offset + i * 4, values[i]);
    }

    private static void WriteFloatArray(byte[] destination, int offset, float[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            int bits = BitConverter.SingleToInt32Bits(values[i]);
            BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(offset + i * 4, 4), bits);
        }
    }
}
