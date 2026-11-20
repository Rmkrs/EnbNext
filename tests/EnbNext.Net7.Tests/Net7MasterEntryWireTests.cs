using System.Buffers.Binary;
using EnbNext.Net7.Protocol;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7MasterEntryWireTests
{
    [Fact]
    public void MasterLoginResponseConsumesSixBytePrefixAndIgnoresTrailingBytes()
    {
        byte[] payload = new byte[11];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), 0x11223344);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), 3801);
        payload.AsSpan(6).Fill(0xAA);

        Net7MasterLoginResponse decoded = Net7MasterLoginResponse.Decode(payload);
        Assert.Equal(0x11223344u, decoded.RetainedMasterResponseDword);
        Assert.Equal((ushort)3801, decoded.CandidateGameplayPort);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void ShortMasterLoginResponseFailsClosed(int length)
        => Assert.Throws<InvalidDataException>(() => Net7MasterLoginResponse.Decode(new byte[length]));

    [Fact]
    public void Embedded2010ParsesDeclaredInnerFrameAndIgnoresTrailingOuterBytes()
    {
        byte[] outer = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(outer.AsSpan(0, 2), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(outer.AsSpan(2, 2), 0x0005);
        BinaryPrimitives.WriteUInt32LittleEndian(outer.AsSpan(4, 4), 0xAABBCCDD);
        outer.AsSpan(8).Fill(0xEE);

        Packet packet = Net7EmbeddedClientFrame.Decode(outer);
        Assert.Equal(0x0005, packet.Header.Opcode);
        Assert.Equal(8, packet.Header.Size);
        Assert.Equal(new byte[] { 0xDD, 0xCC, 0xBB, 0xAA }, packet.Payload.ToArray());
    }

    [Fact]
    public void Embedded2010RejectsSentinelLengthOutsideCurrentRange()
    {
        byte[] outer = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(outer.AsSpan(0, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(outer.AsSpan(2, 2), 0x0097);
        Assert.Throws<InvalidDataException>(() => Net7EmbeddedClientFrame.Decode(outer));
    }

    [Fact]
    public void ProductionSectorEntryApiDoesNotAcceptCallerSuppliedGalaxyMapState()
    {
        var publicEntryMethods = typeof(Net7Session)
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Where(method => method.Name == nameof(Net7Session.EnterSelectedCharacterAsync))
            .ToArray();

        Assert.Single(publicEntryMethods);
        Assert.DoesNotContain(
            publicEntryMethods[0].GetParameters(),
            parameter => parameter.ParameterType == typeof(Net7GalaxyMapData));
    }

    [Fact]
    public void CurrentGalaxyMapFixtureMaterializesExactNativeInventory()
    {
        Net7GalaxyMapData map = Net7GalaxyMapCodec.DecodeCurrentGeometry(TestProtocolFixtures.BuildCurrentGalaxyMap());
        Assert.Equal(63, map.AuxiliaryBlob.Length);
        Assert.Equal(19, map.Systems.Count);
        Assert.Equal(86, map.Sectors.Count);
        Assert.Equal(199, map.Gates.Count);
        Assert.Equal((uint)6, map.Sectors[0].Subtype);
        Assert.Equal((uint)7, map.Sectors[75].Subtype);
        Assert.Equal((uint)8, map.Sectors[82].Subtype);
    }

    [Fact]
    public void CurrentGalaxyMapRejectsWrongFileGeometry()
        => Assert.Throws<InvalidDataException>(() => Net7GalaxyMapCodec.DecodeCurrentGeometry(new byte[12646]));


    [Fact]
    public void GalaxyMapAcceptsValidSyntheticContentWithoutReleaseFingerprint()
    {
        byte[] synthetic = TestProtocolFixtures.BuildCurrentGalaxyMap();
        var map = Net7GalaxyMapCodec.DecodePinnedCurrentAsset(synthetic);
        Assert.Equal(19, map.Systems.Count);
    }

    [Theory]
    [InlineData(9999u, 0x3004)]
    [InlineData(10000u, 0x3008)]
    [InlineData(0xFFFFFFFFu, 0x3004)]
    public void StartCompanionSelectorUsesCurrentSignedSectorComparison(uint sector, int expectedOpcode)
        => Assert.Equal((ushort)expectedOpcode, Net7SectorEntryWire.SelectStartCompanionOpcode(sector));

    [Fact]
    public void SectorEntryWireEmitsExactFourByteFields()
    {
        Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11 }, Net7SectorEntryWire.BuildRequestTime(0x11223344));
        Assert.Equal(new byte[] { 0x88, 0x77, 0x66, 0x55 }, Net7SectorEntryWire.BuildStartAck(0x55667788));
        Assert.Equal(0x55667788u, Net7SectorEntryWire.DecodeStartPlayerId([0x88, 0x77, 0x66, 0x55]));
    }
    [Fact]
    public void StartIgnoresTrailingBytesAndEchoesOnlyPlayerId()
    {
        uint playerId = Net7SectorEntryWire.DecodeStartPlayerId([0x88, 0x77, 0x66, 0x55, 0xFF, 0x00]);
        Assert.Equal(0x55667788u, playerId);
        Assert.Equal(new byte[] { 0x88, 0x77, 0x66, 0x55 }, Net7SectorEntryWire.BuildStartAck(playerId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void ShortStartHasNoDefinedPlayerId(int length)
        // 008A66A0 reads +0C into uninitialized packet storage; the routed pump
        // drops the undecodable frame and keeps receiving.
        => Assert.Throws<InvalidDataException>(() => Net7SectorEntryWire.DecodeStartPlayerId(new byte[length]));
}
