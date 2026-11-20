using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.Presentation;

public sealed record Net7ChangeBaseAssetUpdate(
    uint GameId,
    int BaseAsset,
    float Scale,
    float HsvH,
    float HsvS,
    float HsvV);

public sealed record Net7ServerParametersPresentationUpdate(
    byte[] RawPayload,
    float FogNear,
    float FogFar,
    byte LightBackdrop,
    byte FogBackdrop,
    byte SwapBackdrop,
    float BackdropFogNear,
    float BackdropFogFar,
    short BackdropBaseAsset,
    uint SectorNum)
{
    // Compatibility aliases retained for pre- presentation code/tests.
    public short EnvironmentBaseAsset => BackdropBaseAsset;
    public uint FinalRawValue => SectorNum;
}

public sealed record Net7AvatarDescriptionUpdate(uint GameId, Net7AvatarData Avatar);

public sealed record Net7ShipPartAttribute(Vector3 Color, byte ByteValue, uint DwordValue);

public sealed record Net7ShipCustomization(
    uint Profession,
    uint TechLevel,
    uint HullStyle,
    uint WingStyle,
    uint SelectedDecal,
    string ShipName,
    Vector3 ShipNameColor,
    IReadOnlyList<Net7ShipPartAttribute> PartAttributes,
    byte[] RawPayload);

public sealed record Net7ShipRecustomizeStart(
    Net7ShipCustomization Customization,
    IReadOnlyList<uint> AuxiliaryDwords,
    IReadOnlyList<uint> TrailingNetworkValues);

public sealed record Net7AvatarRecustomizeStart(
    IReadOnlyList<uint> OptionDwords,
    uint TrailingNetworkValue);

public sealed record Net7AvatarRecustomizeUpdate(
    Net7AvatarData Avatar,
    uint TrailingNetworkValue);

public sealed record Net7StarbaseAvatarState(
    uint AvatarId,
    uint RoomType,
    float Orientation,
    Vector3 Position,
    uint ActionFlag)
{
    public long MotionRevision {get;init;}
    public IReadOnlyList<Net7StarbaseAvatarPose> MotionHistory {get;init;}=[];
}
public sealed record Net7StarbaseAvatarPose(long Revision,float Orientation,Vector3 Position,uint ActionFlag);

// Preserve the order of 009E, 00A0 and appearance replacement for the room
// consumer. A latest-pose dictionary cannot represent leave/re-enter ordering.
public sealed record Net7StationAvatarEvent(ulong Sequence, Net7StarbaseAvatarState? Pose=null,
    Net7StarbaseRoomUpdate? Departure=null, uint? ReplacedAvatarId=null, Net7AvatarData? Avatar=null,
    Net7StationGuildState? Guild=null, Net7StationSpeakerEvent? Speaker=null);
public sealed record Net7StationSpeakerEvent(string Identity,byte Type,Vector3? Color=null);
public sealed record Net7StationGuildState(string? Name,int Rank,string? RankName,
    Vector3? SameColor,Vector3? OtherColor,string? LocalName);

/// <summary>
/// Codecs for current inbound-only retail packet families. Numeric byte
/// order follows the current client decoders, including the explicit network
/// u32 fields in the recustomization packets.
/// </summary>
public static class Net7PresentationWire
{
    public const int ChangeBaseAssetPayloadSize = 24;
    public const int ServerParametersPayloadSize = 70;
    public const int ServerParametersFogNearOffset = 24;
    public const int ServerParametersFogFarOffset = 28;
    public const int ServerParametersLightBackdropOffset = 36;
    public const int ServerParametersFogBackdropOffset = 37;
    public const int ServerParametersSwapBackdropOffset = 38;
    public const int ServerParametersBackdropFogNearOffset = 39;
    public const int ServerParametersBackdropFogFarOffset = 43;
    public const int ServerParametersBackdropBaseAssetOffset = 64;
    public const int ServerParametersSectorNumOffset = 66;
    public const int ShipCustomizationSize = 194;
    public const int ShipRecustomizeAuxSize = 48;
    public const int ShipRecustomizeTrailingCount = 5;
    public const int ShipRecustomizeStartPayloadSize = ShipCustomizationSize + ShipRecustomizeAuxSize + ShipRecustomizeTrailingCount * 4;
    public const int AvatarRecustomizeOptionsSize = 56;
    public const int AvatarRecustomizeStartPayloadSize = AvatarRecustomizeOptionsSize + 4;
    public const int StarbaseAvatarChangePayloadSize = 28;
    public const int AvatarDescriptionCompatibilityTailSize = 15;

    public static Net7ChangeBaseAssetUpdate DecodeChangeBaseAsset(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, ChangeBaseAssetPayloadSize, "0x0026 Change Base Asset");
        return new Net7ChangeBaseAssetUpdate(
            ReadU32(payload, 0),
            BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4)),
            ReadF32(payload, 8),
            ReadF32(payload, 12),
            ReadF32(payload, 16),
            ReadF32(payload, 20));
    }

    public static uint DecodeClientAvatarId(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, 4, "0x0037 Client Avatar");
        return ReadU32(payload, 0);
    }

    public static Net7ServerParametersPresentationUpdate DecodeServerParameters(ReadOnlySpan<byte> payload)
    {
        // Current 0x008A5C10 reads exactly 70 sequential wire bytes. Object
        // alignment makes its in-memory offsets sparse. ServerParametersPacketProcess
        // copies object +0x32 to SClient SwapBackdrop; sequential object fields begin
        // at +0x0C, placing that byte at wire offset 38. The signed BaseAsset is at
        // wire byte 64, followed by one final raw DWORD.
        RequirePrefix(payload, ServerParametersPayloadSize, "0x0042 Server Parameters");
        return new Net7ServerParametersPresentationUpdate(
            payload[..ServerParametersPayloadSize].ToArray(),
            ReadF32(payload, ServerParametersFogNearOffset),
            ReadF32(payload, ServerParametersFogFarOffset),
            payload[ServerParametersLightBackdropOffset],
            payload[ServerParametersFogBackdropOffset],
            payload[ServerParametersSwapBackdropOffset],
            ReadF32(payload, ServerParametersBackdropFogNearOffset),
            ReadF32(payload, ServerParametersBackdropFogFarOffset),
            BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(ServerParametersBackdropBaseAssetOffset, 2)),
            ReadU32(payload, ServerParametersSectorNumOffset));
    }

    public static uint DecodeClientShipId(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, 4, "0x0047 Client Ship");
        return ReadU32(payload, 0);
    }

    public static Net7AvatarDescriptionUpdate DecodeAvatarDescription(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4 + Net7AvatarRosterCodec.AvatarSkeletonOffset + 1)
            throw new InvalidDataException("0x0061 Avatar Description is shorter than GameID plus minimum AvatarData geometry.");
        uint gameId = ReadU32(payload, 0);
        (Net7AvatarData avatar, _) = Net7AvatarRosterCodec.DecodeAvatarData(payload[4..], "0x0061 Avatar Description AvatarData");

        // 00877A60 reads GameID and AvatarData; 00733B10 consumes that appearance,
        // without requiring the packet reader to reach its end.
        return new Net7AvatarDescriptionUpdate(gameId, avatar);
    }

    public static Net7ShipRecustomizeStart DecodeShipRecustomizeStart(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, ShipRecustomizeStartPayloadSize, "0x0081 Recustomize Ship Start");
        Net7ShipCustomization customization = DecodeShipCustomization(payload[..ShipCustomizationSize]);

        var aux = new uint[ShipRecustomizeAuxSize / 4];
        int offset = ShipCustomizationSize;
        for (int i = 0; i < aux.Length; i++, offset += 4)
            aux[i] = ReadU32(payload, offset);

        var trailing = new uint[ShipRecustomizeTrailingCount];
        for (int i = 0; i < trailing.Length; i++, offset += 4)
            trailing[i] = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4));

        return new Net7ShipRecustomizeStart(customization, aux, trailing);
    }

    public static Net7AvatarRecustomizeStart DecodeAvatarRecustomizeStart(ReadOnlySpan<byte> payload)
    {
        // 008492F0 initializes all fourteen options; 008A0710 initializes the
        // final network DWORD. 00849960 keeps those bytes on a short read.
        Span<byte> initialized = stackalloc byte[AvatarRecustomizeStartPayloadSize];
        initialized.Clear();
        payload[..Math.Min(payload.Length, initialized.Length)].CopyTo(initialized);
        var options = new uint[AvatarRecustomizeOptionsSize / 4];
        for (int i = 0; i < options.Length; i++)
            options[i] = ReadU32(initialized, i * 4);
        uint trailing = BinaryPrimitives.ReadUInt32BigEndian(initialized.Slice(AvatarRecustomizeOptionsSize, 4));
        return new Net7AvatarRecustomizeStart(options, trailing);
    }

    public static Net7AvatarRecustomizeUpdate DecodeAvatarRecustomizeUpdate(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < Net7AvatarRosterCodec.AvatarSkeletonOffset + 1)
            throw new InvalidDataException("0x0085 Recustomize Avatar Update is shorter than minimum AvatarData.");

        // 008A0F20 reads this field after AvatarData, not from the packet end.
        (Net7AvatarData avatar, int consumed) = Net7AvatarRosterCodec.DecodeAvatarData(payload, "0x0085 Recustomize Avatar Update AvatarData");
        Span<byte> trailingBytes = stackalloc byte[4];
        trailingBytes.Clear();
        payload.Slice(consumed, Math.Min(4, payload.Length - consumed)).CopyTo(trailingBytes);
        uint trailing = BinaryPrimitives.ReadUInt32BigEndian(trailingBytes);
        return new Net7AvatarRecustomizeUpdate(avatar, trailing);
    }

    public static Net7StarbaseAvatarState DecodeStarbaseAvatarChange(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, StarbaseAvatarChangePayloadSize, "0x009E Starbase Avatar Change");
        return new Net7StarbaseAvatarState(
            ReadU32(payload, 0),
            // Inbound 009E differs from outbound 009D. Retail 008A3A40
            // reads sequentially; 0073AA79 consumes the final DWORD as room.
            ReadU32(payload, 24),
            ReadF32(payload, 4),
            new Vector3(ReadF32(payload, 8), ReadF32(payload, 12), ReadF32(payload, 16)),
            ReadU32(payload, 20));
    }

    private static Net7ShipCustomization DecodeShipCustomization(ReadOnlySpan<byte> payload)
    {
        RequirePrefix(payload, ShipCustomizationSize, "ShipCustomization");
        // Current serializer order is five raw DWORDs, 26 ship-name
        // bytes, float3 name color, then four PartAttributes groups containing
        // two 17-byte entries apiece.
        uint profession = ReadU32(payload, 0);
        uint techLevel = ReadU32(payload, 4);
        uint hullStyle = ReadU32(payload, 8);
        uint wingStyle = ReadU32(payload, 12);
        uint selectedDecal = ReadU32(payload, 16);
        string shipName = ReadFixedLatin1(payload.Slice(20, 26));
        Vector3 shipNameColor = new(ReadF32(payload, 46), ReadF32(payload, 50), ReadF32(payload, 54));

        var parts = new Net7ShipPartAttribute[8];
        int offset = 58;
        for (int i = 0; i < parts.Length; i++)
        {
            Vector3 color = new(ReadF32(payload, offset), ReadF32(payload, offset + 4), ReadF32(payload, offset + 8));
            byte byteValue = payload[offset + 12];
            uint dwordValue = ReadU32(payload, offset + 13);
            parts[i] = new Net7ShipPartAttribute(color, byteValue, dwordValue);
            offset += 17;
        }
        if (offset != ShipCustomizationSize)
            throw new InvalidDataException("ShipCustomization decoder geometry did not consume exactly 194 bytes.");

        return new Net7ShipCustomization(
            profession, techLevel, hullStyle, wingStyle, selectedDecal,
            shipName, shipNameColor, parts, payload.ToArray());
    }

    private static uint ReadU32(ReadOnlySpan<byte> payload, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));

    private static float ReadF32(ReadOnlySpan<byte> payload, int offset)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4)));

    private static string ReadFixedLatin1(ReadOnlySpan<byte> data)
    {
        int nul = data.IndexOf((byte)0);
        if (nul < 0)
            nul = data.Length;
        return Encoding.Latin1.GetString(data[..nul]);
    }

    private static void RequirePrefix(ReadOnlySpan<byte> payload, int expected, string label)
    {
        if (payload.Length < expected)
            throw new InvalidDataException($"{label} payload must contain at least {expected} bytes; received {payload.Length}.");
    }
}
