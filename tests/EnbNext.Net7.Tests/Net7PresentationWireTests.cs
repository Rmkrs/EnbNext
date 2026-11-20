using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.Tests;

public sealed class Net7PresentationWireTests
{
    [Fact]
    public void AvatarRecustomizeOptionsPreserveInitializedBytesOnShortRead()
    {
        var empty = Net7PresentationWire.DecodeAvatarRecustomizeStart([]);
        Assert.All(empty.OptionDwords, value => Assert.Equal(0u, value));
        Assert.Equal(0u, empty.TrailingNetworkValue);
        var partial = Net7PresentationWire.DecodeAvatarRecustomizeStart([0x12, 0x34]);
        Assert.Equal(0x3412u, partial.OptionDwords[0]);
        Assert.Equal(0u, partial.OptionDwords[1]);
        byte[] trailing = new byte[58];
        trailing[56] = 0xAB; trailing[57] = 0xCD;
        Assert.Equal(0xABCD0000u, Net7PresentationWire.DecodeAvatarRecustomizeStart(trailing).TrailingNetworkValue);
        var avatar = BuildAvatarData("")[..203];
        Assert.Equal("Kestralion", Net7PresentationWire.DecodeAvatarDescription([1, 0, 0, 0, .. avatar]).Avatar.FirstName);
        Assert.Equal("Kestralion", Net7PresentationWire.DecodeAvatarRecustomizeUpdate(avatar).Avatar.FirstName);
    }

    [Fact]
    public void StationInboundMovementUsesRoomLastAndUpdatesTheSamePlayer()
    {
        // Independent 009E wire fixtures: ID, heading, XYZ, action, room.
        // 008A3A40 constructor + raw 0073AA73..0073AABB consumer.
        var world = new EnbNext.Net7.World.Net7WorldState();
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x009E,
            Convert.FromHexString("443322110000803F000020410000A0C1000040400100000000000000")), 0);
        var first = world.StationAvatars[0x11223344];
        Assert.Equal(0u, first.RoomType);
        Assert.Equal(1f, first.Orientation);
        Assert.Equal(new Vector3(10, -20, 3), first.Position);
        Assert.Equal(1u, first.ActionFlag);
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x009E,
            Convert.FromHexString("44332211000000BF00004041000090C1000080400000000003000000")), 1);
        var moved = Assert.Single(world.StationAvatars).Value;
        Assert.Equal(3u, moved.RoomType);
        Assert.Equal(-.5f, moved.Orientation);
        Assert.Equal(new Vector3(12, -18, 4), moved.Position);
        Assert.Equal(0u, moved.ActionFlag);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void I05FixedInboundFamiliesDecodeExactCurrentGeometryAndByteOrder(bool extraTail)
    {
        byte[] change = new byte[Net7PresentationWire.ChangeBaseAssetPayloadSize];
        WriteU32(change, 0, 0x11223344);
        BinaryPrimitives.WriteInt32LittleEndian(change.AsSpan(4, 4), -123456);
        WriteF32(change, 8, 1.5f);
        WriteF32(change, 12, 0.1f);
        WriteF32(change, 16, 0.2f);
        WriteF32(change, 20, 0.3f);
        Net7ChangeBaseAssetUpdate changeDecoded = Net7PresentationWire.DecodeChangeBaseAsset(extraTail ? [.. change, 0xFF, 0xAA, 0x55] : change);
        Assert.Equal(0x11223344u, changeDecoded.GameId);
        Assert.Equal(-123456, changeDecoded.BaseAsset);
        Assert.Equal(1.5f, changeDecoded.Scale);
        Assert.Equal(0.3f, changeDecoded.HsvV);

        Assert.Equal(0xAABBCCDDu, Net7PresentationWire.DecodeClientAvatarId(Convert.FromHexString("DDCCBBAA")));
        Assert.Equal(0x10203040u, Net7PresentationWire.DecodeClientShipId(Convert.FromHexString("40302010")));
        Assert.Throws<InvalidDataException>(() => Net7PresentationWire.DecodeClientAvatarId(new byte[3]));
        Assert.Equal(0u, Net7PresentationWire.DecodeClientShipId(new byte[5]));

        byte[] serverParameters = new byte[Net7PresentationWire.ServerParametersPayloadSize];
        serverParameters[Net7PresentationWire.ServerParametersSwapBackdropOffset] = 1;
        BinaryPrimitives.WriteInt16LittleEndian(serverParameters.AsSpan(64, 2), -321);
        WriteU32(serverParameters, 66, 0x55667788);
        Net7ServerParametersPresentationUpdate parameters = Net7PresentationWire.DecodeServerParameters(extraTail ? [.. serverParameters, 0xFF, 0xAA, 0x55] : serverParameters);
        Assert.Equal((byte)1, parameters.SwapBackdrop);
        Assert.Equal(-321, parameters.EnvironmentBaseAsset);
        Assert.Equal(0x55667788u, parameters.FinalRawValue);
        Assert.Equal(serverParameters, parameters.RawPayload);

        byte[] starbase = BuildStarbaseAvatarChange(7, 8, 1.25f, new Vector3(2, 3, 4), 9);
        Net7StarbaseAvatarState avatar = Net7PresentationWire.DecodeStarbaseAvatarChange(extraTail ? [.. starbase, 0xFF, 0xAA, 0x55] : starbase);
        Assert.Equal(7u, avatar.AvatarId);
        Assert.Equal(8u, avatar.RoomType);
        Assert.Equal(1.25f, avatar.Orientation);
        Assert.Equal(new Vector3(2, 3, 4), avatar.Position);
        Assert.Equal(9u, avatar.ActionFlag);

        Assert.Throws<InvalidDataException>(() => Net7PresentationWire.DecodeChangeBaseAsset(change[..^1]));
        Assert.Throws<InvalidDataException>(() => Net7PresentationWire.DecodeServerParameters(serverParameters[..^1]));
        Assert.Throws<InvalidDataException>(() => Net7PresentationWire.DecodeStarbaseAvatarChange(starbase[..^1]));
    }

    [Fact]
    public void AvatarDescriptionAndAvatarRecustomizationReuseCurrentDynamicAvatarDataGeometry()
    {
        byte[] avatar = BuildAvatarData("DYNAMIC_SKELETON");

        byte[] description = new byte[4 + avatar.Length];
        WriteU32(description, 0, 0x01020304);
        avatar.CopyTo(description, 4);
        Net7AvatarDescriptionUpdate decodedDescription = Net7PresentationWire.DecodeAvatarDescription(description);
        Assert.Equal(0x01020304u, decodedDescription.GameId);
        Assert.Equal("Kestralion", decodedDescription.Avatar.FirstName);
        Assert.Equal("DYNAMIC_SKELETON", decodedDescription.Avatar.Skeleton);

        byte[] update = new byte[avatar.Length + 4];
        avatar.CopyTo(update, 0);
        BinaryPrimitives.WriteUInt32BigEndian(update.AsSpan(avatar.Length, 4), 0x11223344);
        Net7AvatarRecustomizeUpdate decodedUpdate = Net7PresentationWire.DecodeAvatarRecustomizeUpdate(update);
        Assert.Equal("DYNAMIC_SKELETON", decodedUpdate.Avatar.Skeleton);
        Assert.Equal(0x11223344u, decodedUpdate.TrailingNetworkValue);

        byte[] badDescription = [.. description, 0xFF];
        Assert.Equal(decodedDescription.Avatar.Skeleton, Net7PresentationWire.DecodeAvatarDescription(badDescription).Avatar.Skeleton);
        byte[] badUpdate = [.. avatar, 0xFF, 0x11, 0x22, 0x33, 0x44];
        Assert.Equal(0xFF112233u, Net7PresentationWire.DecodeAvatarRecustomizeUpdate(badUpdate).TrailingNetworkValue);
        Assert.Equal(0u, Net7PresentationWire.DecodeAvatarRecustomizeUpdate(avatar).TrailingNetworkValue);
        Assert.Equal(0xAABB0000u, Net7PresentationWire.DecodeAvatarRecustomizeUpdate([.. avatar, 0xAA, 0xBB]).TrailingNetworkValue);
    }

    [Fact]
    public void CurrentRetailAvatarDescriptionCompatibilityTailIsAcceptedAtPacketBoundary()
    {
        // Exact 260-byte payload from the current Kestralion 0x0061 startup frame.
        // The retail packet is 264 bytes including its 4-byte length/opcode header.
        byte[] payload = Convert.FromHexString(
            "0A4200404B65737472616C696F6E0000000000000000000000000000000000000000000000000000000000000000000000020200000000000000000000000000000002000202000000000000000003000000003F0000003F0000803FBFB6363FB7B3333FB7B3333FBFB6363FB7B3333FB7B3333F" +
            "09F9F83ECCD2523FD5CD4D3F0000803F0000803FE4DB5B3FA8AB2B3F7190903DEBE0E03DBCCACA3EC5C9493FC5C5453FA7AD2D3F6FA0A03D938C0C3EDDCECE3ECDCA4A3FC5C7473FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF000000803F000000000000000000000000000000000000803F00000000000000000000000000000000000000000000000000803F0000803F");

        Assert.Equal(260, payload.Length);
        Net7AvatarDescriptionUpdate decoded = Net7PresentationWire.DecodeAvatarDescription(payload);
        Assert.Equal(0x4000420Au, decoded.GameId);
        Assert.Equal("Kestralion", decoded.Avatar.FirstName);
        Assert.Equal(string.Empty, decoded.Avatar.Skeleton);

        // The observed 15 bytes are not a native admission rule.
        Assert.Equal(decoded.Avatar.FirstName, Net7PresentationWire.DecodeAvatarDescription(payload[..^1]).Avatar.FirstName);
        Assert.Equal(decoded.Avatar.FirstName, Net7PresentationWire.DecodeAvatarDescription([.. payload, 0xEE]).Avatar.FirstName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShipRecustomizationPreserves194ByteRecord48ByteAuxAndFiveNetworkDwords(bool extraTail)
    {
        byte[] payload = BuildShipRecustomization();
        Net7ShipRecustomizeStart decoded = Net7PresentationWire.DecodeShipRecustomizeStart(extraTail ? [.. payload, 0xFF, 0xAA, 0x55] : payload);

        Assert.Equal(1u, decoded.Customization.Profession);
        Assert.Equal(2u, decoded.Customization.TechLevel);
        Assert.Equal(3u, decoded.Customization.HullStyle);
        Assert.Equal(4u, decoded.Customization.WingStyle);
        Assert.Equal(5u, decoded.Customization.SelectedDecal);
        Assert.Equal("I05 Ship", decoded.Customization.ShipName);
        Assert.Equal(new Vector3(0.25f, 0.5f, 0.75f), decoded.Customization.ShipNameColor);
        Assert.Equal(8, decoded.Customization.PartAttributes.Count);
        Assert.Equal(0x80u, decoded.Customization.PartAttributes[0].DwordValue);
        Assert.Equal(0x87u, decoded.Customization.PartAttributes[7].DwordValue);
        Assert.Equal(payload[..Net7PresentationWire.ShipCustomizationSize], decoded.Customization.RawPayload);

        Assert.Equal(12, decoded.AuxiliaryDwords.Count);
        Assert.Equal(0x1000u, decoded.AuxiliaryDwords[0]);
        Assert.Equal(0x100Bu, decoded.AuxiliaryDwords[11]);
        Assert.Equal(new uint[] { 0xA0A1A2A3u, 0xB0B1B2B3u, 0xC0C1C2C3u, 0xD0D1D2D3u, 0xE0E1E2E3u }, decoded.TrailingNetworkValues);

        Assert.Throws<InvalidDataException>(() => Net7PresentationWire.DecodeShipRecustomizeStart(payload[..^1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AvatarRecustomizeStartPreservesFourteenRawDwordsAndNetworkTrailingValue(bool extraTail)
    {
        byte[] payload = new byte[Net7PresentationWire.AvatarRecustomizeStartPayloadSize];
        for (int i = 0; i < 14; i++)
            WriteU32(payload, i * 4, (uint)(0x2000 + i));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(56, 4), 0xDEADBEEF);

        Net7AvatarRecustomizeStart decoded = Net7PresentationWire.DecodeAvatarRecustomizeStart(extraTail ? [.. payload, 0xFF, 0xAA, 0x55] : payload);
        Assert.Equal(14, decoded.OptionDwords.Count);
        Assert.Equal(0x2000u, decoded.OptionDwords[0]);
        Assert.Equal(0x200Du, decoded.OptionDwords[13]);
        Assert.Equal(0xDEADBEEFu, decoded.TrailingNetworkValue);
        Assert.Equal(decoded.TrailingNetworkValue & 0xFFFFFF00u, Net7PresentationWire.DecodeAvatarRecustomizeStart(payload[..^1]).TrailingNetworkValue);
    }

    internal static byte[] BuildAvatarData(string skeleton = "S")
    {
        byte[] roster = TestProtocolFixtures.BuildRoster(slot0Skeleton: skeleton);
        int size = Net7AvatarRosterCodec.AvatarDataBaseSize + Encoding.Latin1.GetByteCount(skeleton);
        return roster.AsSpan(Net7AvatarRosterCodec.SlotMetaSize, size).ToArray();
    }

    internal static byte[] BuildShipRecustomization()
    {
        byte[] payload = new byte[Net7PresentationWire.ShipRecustomizeStartPayloadSize];
        WriteU32(payload, 0, 1);
        WriteU32(payload, 4, 2);
        WriteU32(payload, 8, 3);
        WriteU32(payload, 12, 4);
        WriteU32(payload, 16, 5);
        Encoding.Latin1.GetBytes("I05 Ship\0").CopyTo(payload, 20);
        WriteF32(payload, 46, 0.25f);
        WriteF32(payload, 50, 0.5f);
        WriteF32(payload, 54, 0.75f);
        int offset = 58;
        for (int i = 0; i < 8; i++)
        {
            WriteF32(payload, offset, i + 0.1f);
            WriteF32(payload, offset + 4, i + 0.2f);
            WriteF32(payload, offset + 8, i + 0.3f);
            payload[offset + 12] = (byte)(0x40 + i);
            WriteU32(payload, offset + 13, (uint)(0x80 + i));
            offset += 17;
        }
        Assert.Equal(Net7PresentationWire.ShipCustomizationSize, offset);

        for (int i = 0; i < 12; i++)
            WriteU32(payload, 194 + i * 4, (uint)(0x1000 + i));
        uint[] trailing = [0xA0A1A2A3u, 0xB0B1B2B3u, 0xC0C1C2C3u, 0xD0D1D2D3u, 0xE0E1E2E3u];
        for (int i = 0; i < trailing.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(242 + i * 4, 4), trailing[i]);
        return payload;
    }

    internal static byte[] BuildStarbaseAvatarChange(uint avatarId, uint room, float orientation, Vector3 position, uint actionFlag)
    {
        byte[] payload = new byte[28];
        WriteU32(payload, 0, avatarId);
        WriteF32(payload, 4, orientation);
        WriteF32(payload, 8, position.X);
        WriteF32(payload, 12, position.Y);
        WriteF32(payload, 16, position.Z);
        WriteU32(payload, 20, actionFlag);
        WriteU32(payload, 24, room);
        return payload;
    }

    internal static void WriteU32(byte[] data, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);

    internal static void WriteF32(byte[] data, int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));
}
