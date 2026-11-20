using System.Buffers.Binary;
using System.Text;
using EnbNext.Net7.Protocol;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7AvatarManagementWireTests
{
    [Fact]
    public void CreatePayload_IsExactFixedLogin07GeometryAndOuterFrame()
    {
        Net7AvatarData avatar = BuildCreateAvatar();
        byte[] shipBytes = Enumerable.Range(0, Net7ShipAppearanceData.SerializedSize).Select(i => (byte)i).ToArray();
        var request = new Net7AvatarCreateRequest(
            0x01020304,
            2,
            0xA5,
            avatar,
            new Net7ShipAppearanceData(shipBytes));

        byte[] payload = Net7AccountWire.BuildAvatarCreatePayload(request, "pilot");
        Assert.Equal(0x21B, payload.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, payload.AsSpan(0x000, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 2 }, payload.AsSpan(0x004, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 0xA5 }, payload.AsSpan(0x008, 4).ToArray());
        Assert.Equal("pilot", Encoding.Latin1.GetString(payload.AsSpan(0x00C, 5)));
        Assert.Equal(0, payload[0x011]);
        Assert.Equal(0, payload[0x04C]);
        Assert.Equal(Net7AvatarCreateCodec.EncodeAvatarData(avatar), payload.AsSpan(0x04D, 0x10C).ToArray());
        Assert.Equal(shipBytes, payload.AsSpan(0x159, 0xC2).ToArray());

        byte[] wire = Packet.ForOpcode(0x200B, payload).ToWireBytes();
        Assert.Equal(0x21F, wire.Length);
        Assert.Equal(0x21F, BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(0, 2)));
        Assert.Equal(0x200B, BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(2, 2)));
        Assert.Equal(payload, wire.AsSpan(4).ToArray());
    }

    [Fact]
    public void CreateUsername_UsesRetail65ByteTruncateTerminateAndPaddingRule()
    {
        var request = new Net7AvatarCreateRequest(
            7,
            0,
            1,
            BuildCreateAvatar(),
            new Net7ShipAppearanceData(new byte[Net7ShipAppearanceData.SerializedSize]));

        byte[] longPayload = Net7AccountWire.BuildAvatarCreatePayload(request, new string('x', 80));
        Assert.Equal(Enumerable.Repeat((byte)'x', 64), longPayload.AsSpan(0x00C, 64).ToArray());
        Assert.Equal(0, longPayload[0x04C]);

        byte[] shortPayload = Net7AccountWire.BuildAvatarCreatePayload(request, "abc");
        Assert.Equal("abc", Encoding.Latin1.GetString(shortPayload.AsSpan(0x00C, 3)));
        Assert.All(shortPayload.AsSpan(0x00F, 62).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void CreateAvatarData_IsExactly10CAndUsesClosedLittleEndianFields()
    {
        Net7AvatarData avatar = BuildCreateAvatar();
        byte[] data = Net7AvatarCreateCodec.EncodeAvatarData(avatar);

        Assert.Equal(0x10C, data.Length);
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x028, 4)));
        Assert.Equal(0xAB, data[0x02C]);
        Assert.Equal(4, data[0x02D]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x02E, 4)));
        Assert.Equal(new string('S', 27), Encoding.Latin1.GetString(data.AsSpan(0x0C8, 27)));
        Assert.Equal(0, data[0x0E3]);
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x0E4, 4)));
        Assert.Equal(204u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x108, 4)));
    }

    [Fact]
    public void CreateAvatarData_MatchesCompleteClosedLogin06SegmentByteForByte()
    {
        byte[] rosterPayload = TestProtocolFixtures.BuildRoster(slot0Skeleton: new string('S', 27));
        Net7AvatarData avatar = Net7AvatarRosterCodec.Decode(rosterPayload).Slots[0].Avatar;

        byte[] encoded = Net7AvatarCreateCodec.EncodeAvatarData(avatar);
        byte[] exactRosterSegment = rosterPayload
            .AsSpan(Net7AvatarRosterCodec.SlotMetaSize, Net7AvatarCreateCodec.MaximumCreateAvatarDataSize)
            .ToArray();

        Assert.Equal(exactRosterSegment, encoded);
    }

    [Theory]
    [InlineData(28)]
    [InlineData(100)]
    public void CreateAvatarData_RejectsOverflowOfFixedProxyEnvelope(int length)
    {
        Net7AvatarData avatar = BuildCreateAvatar() with { Skeleton = new string('S', length) };
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => Net7AvatarCreateCodec.EncodeAvatarData(avatar));
        Assert.Contains("27-byte capacity", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(26)]
    public void CreatePayload_AppendsShipAfterActualAvatarAndZeroFillsEnvelopeTail(int skeletonLength)
    {
        var avatar = BuildCreateAvatar() with { Skeleton = new string('S', skeletonLength) };
        var ship = Enumerable.Range(1, 0xC2).Select(i => (byte)i).ToArray();
        var payload = Net7AccountWire.BuildAvatarCreatePayload(new(7, 0, 0, avatar, new(ship)), "pilot");
        var decoded = Net7AvatarRosterCodec.DecodeAvatarData(payload.AsSpan(0x4D));
        Assert.Equal(0xF1 + skeletonLength, decoded.Consumed);
        Assert.Equal(avatar.Skeleton, decoded.Avatar.Skeleton);
        Assert.Equal(avatar.BodyWeight, decoded.Avatar.BodyWeight);
        Assert.Equal(avatar.HeadWeight, decoded.Avatar.HeadWeight);
        int shipOffset = 0x4D + decoded.Consumed;
        Assert.Equal(ship, payload.AsSpan(shipOffset, 0xC2).ToArray());
        Assert.Equal(0x21B, payload.Length);
        Assert.All(payload.AsSpan(shipOffset + 0xC2).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void ShipAppearance_IsExactC2ImmutableRoundTrip()
    {
        byte[] source = Enumerable.Range(0, Net7ShipAppearanceData.SerializedSize).Select(i => (byte)(255 - i)).ToArray();
        Net7ShipAppearanceData ship = Net7ShipAppearanceData.Decode(source);
        source[0] ^= 0xFF;

        byte[] encoded = ship.ToArray();
        Assert.Equal(0xC2, encoded.Length);
        Assert.NotEqual(source[0], encoded[0]);
        Assert.Equal(encoded, Net7ShipAppearanceData.Decode(encoded).ToArray());
        Assert.Throws<ArgumentException>(() => new Net7ShipAppearanceData(new byte[0xC1]));
        Assert.Throws<ArgumentException>(() => new Net7ShipAppearanceData(new byte[0xC3]));
    }

    [Fact]
    public void CanonicalRetailGlobalError_IsTwoNetworkOrderU32ValuesPlusTerminalNul()
    {
        byte[] payload = Net7GlobalError.EncodeDirect0075(0x01020304, 0x11223344, "Denied");

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, payload.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, payload.AsSpan(4, 4).ToArray());
        Assert.Equal("Denied", Encoding.Latin1.GetString(payload.AsSpan(8, 6)));
        Assert.Equal(0, payload[^1]);

        Net7GlobalError decoded = Net7GlobalError.DecodeDirect0075(payload);
        Assert.Equal(0x01020304u, decoded.OpaqueField0);
        Assert.Equal(0x11223344u, decoded.Code);
        Assert.Equal("Denied", decoded.Message);
        Assert.False(decoded.MalformedTerminator);
    }

    [Fact]
    public void HistoricalProxyGlobalErrorShim_IsRecognizedAsMalformedAndNotCanonicalized()
    {
        byte[] text = Encoding.Latin1.GetBytes("Denied");
        byte[] historical = new byte[8 + text.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(historical.AsSpan(0, 4), checked((uint)text.Length));
        BinaryPrimitives.WriteUInt32BigEndian(historical.AsSpan(4, 4), 14);
        text.CopyTo(historical, 8);

        Net7GlobalError audited = Net7GlobalError.DecodeDirect0075(historical);
        Assert.True(audited.MalformedTerminator);
        Assert.Equal(0x06000000u, audited.OpaqueField0);
        Assert.Equal(14u, audited.Code);
        Assert.Equal("Denied", audited.Message);

        byte[] canonical = Net7GlobalError.EncodeDirect0075(6, 14, "Denied");
        Assert.Equal(15, canonical.Length);
        Assert.Equal(0, canonical[^1]);
        Assert.Equal(new byte[] { 0, 0, 0, 6 }, canonical.AsSpan(0, 4).ToArray());
    }

    [Fact]
    public void RetailDeleteEnvelope_Slot2IsBigEndianAndRemainsDistinctFrom0049Family()
    {
        byte[] retailBody = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(retailBody, 2);
        Packet globalDelete = Packet.ForOpcode(0x0071, retailBody);
        Packet unrelated0049 = Packet.ForOpcode(0x0049, retailBody);

        Assert.Equal(0x0071, globalDelete.Header.Opcode);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(globalDelete.Payload.Span));
        Assert.Equal(0x0049, unrelated0049.Header.Opcode);
        Assert.NotEqual(globalDelete.Header.Opcode, unrelated0049.Header.Opcode);
    }

    [Fact]
    public void DeletePayload_Slot2AliceMatchesLogin08GoldenBodyAndFrame()
    {
        byte[] payload = Net7AccountWire.BuildAvatarDeletePayload(2, "alice");
        Assert.Equal(new byte[]
        {
            0x00, 0x00, 0x00, 0x02,
            0x05, 0x00,
            0x61, 0x6C, 0x69, 0x63, 0x65,
        }, payload);

        byte[] wire = Packet.ForOpcode(0x200D, payload).ToWireBytes();
        Assert.Equal(new byte[] { 0x0F, 0x00, 0x0D, 0x20 }, wire.AsSpan(0, 4).ToArray());
        Assert.Equal(payload, wire.AsSpan(4).ToArray());
    }

    [Fact]
    public void DeletePayload_ExcludesNulAndRejectsUnsafeIdentityLengths()
    {
        byte[] payload = Net7AccountWire.BuildAvatarDeletePayload(1, "pilot");
        Assert.Equal(5, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4, 2)));
        Assert.Equal(11, payload.Length);
        Assert.Equal("pilot", Encoding.Latin1.GetString(payload.AsSpan(6)));

        Assert.Throws<ArgumentException>(() => Net7AccountWire.BuildAvatarDeletePayload(1, "bad\0name"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Net7AccountWire.BuildAvatarDeletePayload(1, new string('x', 65536)));
    }

    private static Net7AvatarData BuildCreateAvatar()
    {
        Net7AvatarData decoded = Net7AvatarRosterCodec.Decode(
            TestProtocolFixtures.BuildRoster(slot0Skeleton: new string('S', 27))).Slots[0].Avatar;
        return decoded with
        {
            AvatarType = 0x11223344,
            UnknownU8 = 0xAB,
            AvatarVersion = 4,
            Race = 2,
            Profession = 5,
            Gender = 1,
            BodyWeight = [100, 101, 102, 103, 104],
            HeadWeight = [200, 201, 202, 203, 204],
        };
    }
}
