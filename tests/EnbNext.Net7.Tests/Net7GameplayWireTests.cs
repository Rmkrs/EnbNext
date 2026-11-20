using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7GameplayWireTests
{
    [Theory]
    [InlineData("0500074100424300DEAD")]
    [InlineData("05000741004243FFDEAD")]
    [InlineData("FF7F074100")]
    public void MessageStringIgnoresUnusedTailAndEndsTextAtFirstNul(string payload)
    {
        var message = Net7GameplayWire.DecodeMessageString(
            Convert.FromHexString(payload));
        Assert.Equal(7, message.Type);
        Assert.Equal("A", message.Text);
    }

    [Fact]
    public void GracefulLogoffUsesExactWorld13PayloadAndDestinationMapping()
    {
        Assert.Equal(Convert.FromHexString("44332211"), Net7GameplayWire.BuildLogoffRequest(0x11223344));
        Assert.Equal(3, Net7LogoffSemantics.ToRetailTransitionState(Net7LogoffDestination.Desktop));
        Assert.Equal(1, Net7LogoffSemantics.ToRetailTransitionState(Net7LogoffDestination.LoginScreen));
        Assert.Equal(2, Net7LogoffSemantics.ToRetailTransitionState(Net7LogoffDestination.CharacterSelection));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Net7LogoffSemantics.ToRetailTransitionState((Net7LogoffDestination)0));
    }

    [Fact]
    public void MixedEndianOutboundGoldenVectorsMatchStaticClosedContracts()
    {
        Assert.Equal(
            Convert.FromHexString("1122334401020304A1A2A3A4050607080A0B0C0DDEADBEEF"),
            Net7GameplayWire.BuildInventoryTransfer(0x11223344, 0x01020304, 0xA1A2A3A4, 0x05060708, 0x0A0B0C0D, 0xDEADBEEF));

        Assert.Equal(
            Convert.FromHexString("1122334401020304050607080A0B0C0DDEADBEEF01"),
            Net7GameplayWire.BuildInventorySort(0x11223344, 0x01020304, 0x05060708, 0x0A0B0C0D, 0xDEADBEEF, true));

        Assert.Equal(
            Convert.FromHexString("1122334400000004"),
            Net7GameplayWire.BuildManufacturingMode(0x11223344, Net7ManufacturingMode.Refining));
        Assert.Equal(
            Convert.FromHexString("4433221188776655"),
            Net7GameplayWire.BuildRefineryItem(0x11223344, 0x55667788));
        Assert.Equal(
            Convert.FromHexString("1122334455667788"),
            Net7GameplayWire.BuildMissionRequest(0x11223344, 0x55667788));
        Assert.Equal(
            Convert.FromHexString("11223344"),
            Net7GameplayWire.BuildIncapacitationRequest(0x11223344));
    }

    [Fact]
    public void Full06OutboundDebtBuildersHaveExactGoldenGeometry()
    {
        Assert.Equal(Convert.FromHexString("4433221188776655CCBBAA99"), Net7GameplayWire.BuildDebug(0x11223344, 0x55667788, 0x99AABBCC));
        Assert.Equal(Convert.FromHexString("030007486900"), Net7GameplayWire.BuildMessageString(7, "Hi"));
        Assert.Equal(Convert.FromHexString("4433221102"), Net7GameplayWire.BuildSelectTalkTree(0x11223344, 2));
        Assert.Equal(Convert.FromHexString("112233445566778802000000"), Net7GameplayWire.BuildVerbRequest(0x11223344, 0x55667788, 2));
        Assert.Equal(Convert.FromHexString("4433221188776655"), Net7GameplayWire.BuildUiTrigger(0x11223344, 0x55667788));
        Assert.Equal(Convert.FromHexString("020010000000580044332211"), Net7GameplayWire.BuildGadgetControl("X", 0x10, 0x11223344));

        byte[] ship = Enumerable.Repeat((byte)0xA5, 194).ToArray();
        byte[] shipPacket = Net7GameplayWire.BuildRecustomizeShipDone(ship, 0x11223344);
        Assert.Equal(198, shipPacket.Length);
        Assert.Equal(ship, shipPacket[..194]);
        Assert.Equal(Convert.FromHexString("11223344"), shipPacket[194..]);

        byte[] avatar = new byte[0xF1];
        avatar[0xC8] = 0;
        byte[] avatarPacket = Net7GameplayWire.BuildRecustomizeAvatarDone(avatar, 0x11223344);
        Assert.Equal(0xF5, avatarPacket.Length);
        Assert.Equal(Convert.FromHexString("11223344"), avatarPacket[^4..]);

        Assert.Equal(
            Convert.FromHexString("443322118877665553005542004C00"),
            Net7GameplayWire.BuildPetitionStuck(0x11223344, 0x55667788, "S", "UB", "L"));
        Assert.Equal(
            Convert.FromHexString("0100000002000000AABB4433221101"),
            Net7GameplayWire.BuildGalaxyMap2(1, new byte[] { 0xAA, 0xBB }, 0x11223344, true));

        byte[] starbase = Net7GameplayWire.BuildStarbaseAvatarChange(1, 2, 3f, new Vector3(4, 5, 6), 7);
        Assert.Equal(28, starbase.Length);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(starbase.AsSpan(0, 4)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(starbase.AsSpan(24, 4)));
        Assert.Equal(Convert.FromHexString("010000000200000003000000"), Net7GameplayWire.BuildStarbaseRoomChange(1, 2, 3));
        Assert.Equal(Convert.FromHexString("1122334488776655"), Net7GameplayWire.BuildTriggerEmote(0x11223344, 0x55667788));
        Assert.Equal(Convert.FromHexString("112233445566778800000001"), Net7GameplayWire.BuildConfirmedActionResponse(0x11223344, 0x55667788, true));
        Assert.Equal(Convert.FromHexString("11223344"), Net7GameplayWire.BuildSpendReferralPoint(0x11223344));
        Assert.Equal(Convert.FromHexString("11223344000000015800"), Net7GameplayWire.BuildGmEditorCommand(0x11223344, 1, "X"));
    }

    [Fact]
    public void Combat10AndHulkWireContractsRemainTransportExact()
    {
        Assert.Equal(Convert.FromHexString("4433221100000000"), Net7GameplayWire.BuildHulkOpenRequest(0x11223344));
        Assert.Equal(0x11223344u, Net7GameplayWire.DecodeHulkPermission(Convert.FromHexString("11223344")));
        Assert.Equal(Convert.FromHexString("1122334401"), Net7GameplayWire.BuildDistressBeacon(0x11223344, true));

        Net7HullUpgradeNotice hull = Net7GameplayWire.DecodeHullUpgrade(Convert.FromHexString("4433221188776655"));
        Assert.Equal(new Net7HullUpgradeNotice(0x11223344, 0x55667788), hull);

        byte[] damage = new byte[24];
        WriteSingle(damage, 0, 1.5f);
        WriteSingle(damage, 4, 2.5f);
        WriteU32(damage, 8, 4);
        WriteU32(damage, 12, 3);
        WriteU32(damage, 16, 0x11223344);
        WriteU32(damage, 20, 0x55667788);
        Net7DamagePresentation d = Net7GameplayWire.DecodeClientDamage(damage);
        Assert.Equal(1.5f, d.Damage);
        Assert.Equal(2.5f, d.Modifier);
        Assert.Equal(4u, d.Type);
        Assert.Equal(3u, d.Inflicted);
        Assert.Equal(0x11223344u, d.SourceId);
        Assert.Equal(0x55667788u, d.VictimId);

        byte[] avatar = new byte[0xF1];
        avatar[0xC8] = 0;
        using var stream = new MemoryStream();
        stream.Write(Encoding.Latin1.GetBytes("A\0"));
        stream.Write(avatar);
        foreach (string value in new[] { "B", "C", "D", "E", "F", "G", "H" })
            stream.Write(Encoding.Latin1.GetBytes(value + "\0"));
        Net7IncapacitationDetails incap = Net7GameplayWire.DecodeIncapacitationDetails(stream.ToArray());
        Assert.Equal("A", incap.FirstOuterString);
        Assert.Equal(0xF1, incap.AvatarData.Length);
        Assert.Equal(new[] { "B", "C", "D", "E", "F", "G", "H" }, incap.RemainingOuterStrings);
        Assert.Equal("rescued", Net7GameplayWire.DecodeRescueConfirmation(Encoding.Latin1.GetBytes("rescued\0")));
    }

    [Fact]
    public void PlayerCommandUsesNulInclusiveCStringsAndNullTarget()
    {
        byte[] payload = Net7GameplayWire.BuildPlayerCommand(0x11223344, Net7ChatCommandType.EntireSector, "hello");
        Assert.Equal(15, payload.Length);
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4)));
        Assert.Equal(4, payload[4]);
        Assert.Equal(6, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(5, 2)));
        Assert.Equal(Encoding.Latin1.GetBytes("hello\0"), payload.AsSpan(7, 6).ToArray());
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(13, 2)));
    }

    [Fact]
    public void ClientChatRequestUsesThreeSharedStringsAndRawBlob()
    {
        byte[] payload = Net7GameplayWire.BuildClientChatRequest(
            0x11223344, 0x1E, "a", "bc", string.Empty, new byte[] { 0x90, 0x91 });

        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0, 4)));
        Assert.Equal(0x1Eu, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4)));
        Assert.Equal(Convert.FromHexString("010061020062630000020000009091"), payload.AsSpan(8).ToArray());
    }

    [Fact]
    public void SpecialExtensionDecodersConsumeTheProvenPrefixAndAllowTrailingBytes()
    {
        byte[] prospect = new byte[23];
        WriteU32(prospect, 0, 1);
        WriteU32(prospect, 4, 2);
        WriteU32(prospect, 8, 3);
        WriteU32(prospect, 12, 4);
        WriteU32(prospect, 16, 5);
        prospect[20] = 0xAA;
        Net7ProspectExtension p = Net7GameplayWire.DecodeProspectExtension(prospect);
        Assert.Equal(new Net7ProspectExtension(1, 2, 3, 4, 5), p);

        byte[] tractor = BuildTractor(0x2013, "Ore", duration: 321, speed: 12.5f, new Vector3(1, 2, 3));
        Net7TractorExtension t = Net7GameplayWire.DecodeTractorExtension(0x2013, tractor);
        Assert.Equal("Ore", t.Name);
        Assert.Equal(321u, t.DurationMilliseconds);
        Assert.Equal(12.5f, t.TractorSpeed);
        Assert.Equal(new Vector3(1, 2, 3), t.Position);

        byte[] warp = new byte[40];
        WriteSingle(warp, 0, 1);
        WriteSingle(warp, 4, 2);
        WriteSingle(warp, 8, 3);
        WriteSingle(warp, 12, 0);
        WriteSingle(warp, 16, 0);
        WriteSingle(warp, 20, 0);
        WriteSingle(warp, 24, 1);
        WriteU32(warp, 28, 0x55667788);
        WriteU32(warp, 32, 0x99AABBCC);
        Net7WarpCompletionExtension w = Net7GameplayWire.DecodeWarpCompletion(warp);
        Assert.Equal(new Vector3(1, 2, 3), w.Position);
        Assert.Equal(Quaternion.Identity, w.Orientation);
        Assert.Equal(0x55667788u, w.MovementId);
        Assert.Equal(0x99AABBCCu, w.Timestamp);
    }

    [Fact]
    public void SpecialExtensionDecodersPreserveTractorFloatsAndUnusedTail()
    {
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeProspectExtension(new byte[19]));
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeTractorExtension(0x2013, new byte[23]));
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeWarpCompletion(new byte[35]));

        byte[] tractor = BuildTractor(0x2014, string.Empty, 0, float.NaN, Vector3.Zero);
        Assert.True(float.IsNaN(Net7GameplayWire.DecodeTractorExtension(0x2014, tractor).TractorSpeed));
        byte[] oversizedName = BuildTractor(0x2013, new string('x', 128), 0, 1f, Vector3.Zero);
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeTractorExtension(0x2013, oversizedName));
        // 004020D0 strncpy's the C string into a 118-byte field; 118 fits,
        // 119 overwrites the proxy stack cookie. An embedded NUL ends the name.
        Assert.Equal(new string('x', 118), Net7GameplayWire.DecodeTractorExtension(0x2013,
            BuildTractor(0x2013, new string('x', 118), 0, 1f, Vector3.Zero)).Name);
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeTractorExtension(0x2013,
            BuildTractor(0x2013, new string('x', 119), 0, 1f, Vector3.Zero)));
        Assert.Equal("Ore", Net7GameplayWire.DecodeTractorExtension(0x2013,
            BuildTractor(0x2013, "Ore\0" + new string('x', 120), 0, 1f, Vector3.Zero)).Name);
        byte[] unexplainedTail = BuildTractor(0x2013, "Ore", 0, 1f, Vector3.Zero, trailing: 1);
        Assert.Equal("Ore", Net7GameplayWire.DecodeTractorExtension(0x2013, unexplainedTail).Name);

        byte[] warp = new byte[36];
        WriteSingle(warp, 0, float.PositiveInfinity);
        Assert.True(float.IsPositiveInfinity(Net7GameplayWire.DecodeWarpCompletion(warp).Position.X));
    }

    [Fact]
    public void GuildWirePreservesMixedEndianAndNativePromptDispatch()
    {
        byte[] leader = Net7GameplayWire.BuildGuildLeaderAccept(0x11223344, "Guild", true);
        Assert.Equal(Convert.FromHexString("1122334405004775696C6401"), leader);

        byte[] reply = Net7GameplayWire.BuildGuildSimpleReply(15, 0x11223344, "new");
        Assert.Equal(Convert.FromHexString("0F0000001122334403006E6577"), reply);

        byte[] prompt = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(prompt.AsSpan(0, 4), 20);
        Assert.Equal(20u, Net7GameplayWire.DecodeGuildSimplePrompt(prompt).Type);
        BinaryPrimitives.WriteUInt32LittleEndian(prompt.AsSpan(0, 4), 2);
        Assert.Equal(2u, Net7GameplayWire.DecodeGuildSimplePrompt(prompt).Type);

        byte[] message = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(0, 4), 132);
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeGuildMessage(message));

        byte[] impossibleRanks = Convert.FromHexString("0000000200DEADBEEF");
        Assert.Empty(Net7GameplayWire.DecodeGuildRankNames(impossibleRanks));
    }

    [Fact]
    public void UnknownGuildPromptKeepsPreviousPromptAndAllowsFollowingUpdate()
    {
        var state = new Net7GameplayState();
        byte[] prompt = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(prompt, 20);
        Assert.True(state.Apply(Packet.ForOpcode(0x00CC, prompt)));
        var previous = state.GuildPrompt;
        BinaryPrimitives.WriteUInt32LittleEndian(prompt, 2);
        Assert.True(state.Apply(Packet.ForOpcode(0x00CC, prompt)));
        Assert.Same(previous, state.GuildPrompt);
        BinaryPrimitives.WriteUInt32LittleEndian(prompt, 18);
        Assert.True(state.Apply(Packet.ForOpcode(0x00CC, prompt)));
        Assert.Equal(18u, state.GuildPrompt!.Type);
    }

    [Fact]
    public void GameplayReadersLeaveUnusedTailUnread()
    {
        Assert.Equal(Net7GameplayWire.DecodeHullUpgrade(new byte[8]), Net7GameplayWire.DecodeHullUpgrade(new byte[11]));
        Assert.Equal(Net7GameplayWire.DecodeTradeState(new byte[5]), Net7GameplayWire.DecodeTradeState(new byte[9]));
        Assert.Equal(Net7GameplayWire.DecodeGuildPermissions(new byte[16]), Net7GameplayWire.DecodeGuildPermissions(new byte[19]));
        Assert.Equal(Net7GameplayWire.DecodeGuildRecruitConfirm(new byte[4]), Net7GameplayWire.DecodeGuildRecruitConfirm(new byte[7]));
        Assert.Equal(Net7GameplayWire.DecodeGuildGmRenameConfirm(new byte[4]), Net7GameplayWire.DecodeGuildGmRenameConfirm(new byte[7]));
        Assert.Equal(Net7GameplayWire.DecodeGuildMessage(new byte[8]), Net7GameplayWire.DecodeGuildMessage(new byte[11]));
        Assert.Equal("Rescued", Net7GameplayWire.DecodeRescueConfirmation(Encoding.Latin1.GetBytes("Rescued")));
        Assert.Equal("Rescued", Net7GameplayWire.DecodeRescueConfirmation(Encoding.Latin1.GetBytes("Rescued\0unused")));
    }

    [Fact]
    public void InitializedObjectIdsRetainUnreadBytesAndSwapAfterPartialRead()
    {
        Assert.Equal(uint.MaxValue, Net7GameplayWire.DecodeHulkPermission([]));
        Assert.Equal(0x12FFFFFFu, Net7GameplayWire.DecodeHulkPermission([0x12]));
        Assert.Equal(0u, Net7GameplayWire.DecodeActiveManufacturingObject([]));
        Assert.Equal(0x12000000u, Net7GameplayWire.DecodeActiveManufacturingObject([0x12]));
        Assert.Equal(0x12345678u, Net7GameplayWire.DecodeActiveManufacturingObject([0x12, 0x34, 0x56, 0x78, 0xFF]));
    }

    [Fact]
    public void IncapacitationKeepsAvailableTextAndIgnoresUnusedTail()
    {
        byte[] avatar = new byte[0xF1];
        byte[] prefix = [0, .. avatar];
        var partial = Net7GameplayWire.DecodeIncapacitationDetails([.. prefix, .. Encoding.Latin1.GetBytes("Reply")]);
        Assert.Equal("Reply", partial.RemainingOuterStrings[0]);
        Assert.All(partial.RemainingOuterStrings.Skip(1), value => Assert.Equal(string.Empty, value));
        var complete = Net7GameplayWire.DecodeIncapacitationDetails([.. prefix, 0, 0, 0, 0, 0, 0, 0, 0xFF]);
        Assert.All(complete.RemainingOuterStrings, value => Assert.Equal(string.Empty, value));
    }

    [Fact]
    public void CameraControlRetainsInitializedUnreadFields()
    {
        Assert.Equal(new Net7CameraControl(uint.MaxValue, 0), Net7GameplayWire.DecodeCameraControl([]));
        Assert.Equal(new Net7CameraControl(0x00FFFFFF, 0), Net7GameplayWire.DecodeCameraControl([0]));
        Assert.Equal(new Net7CameraControl(3, 0x12000000), Net7GameplayWire.DecodeCameraControl([0, 0, 0, 3, 0x12]));
        Assert.Equal(new Net7CameraControl(3, 1), Net7GameplayWire.DecodeCameraControl([0, 0, 0, 3, 0, 0, 0, 1, 0xFF]));
    }

    internal static byte[] BuildTractor(
        ushort opcode,
        string name,
        uint duration,
        float speed,
        Vector3 position,
        int trailing = 0,
        uint source = 0xAABBCCDD,
        uint basset = 0x01020304,
        uint effect = 0x05060708,
        uint timestamp = 0x11121314)
    {
        Assert.True(opcode is 0x2013 or 0x2014);
        byte[] nameBytes = Encoding.Latin1.GetBytes(name);
        byte[] payload = new byte[40 + nameBytes.Length + trailing];
        WriteU32(payload, 0, source);
        WriteU32(payload, 4, basset);
        WriteU32(payload, 8, effect);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12, 2), 0x1234);
        WriteU32(payload, 14, timestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(18, 2), checked((ushort)nameBytes.Length));
        nameBytes.CopyTo(payload, 20);
        int tail = 20 + nameBytes.Length;
        WriteU32(payload, tail, duration);
        WriteSingle(payload, tail + 4, speed);
        WriteSingle(payload, tail + 8, position.X);
        WriteSingle(payload, tail + 12, position.Y);
        WriteSingle(payload, tail + 16, position.Z);
        return payload;
    }

    private static void WriteU32(byte[] payload, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), value);

    private static void WriteSingle(byte[] payload, int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));
}
