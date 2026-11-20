using System.Buffers.Binary;
using System.Text;
using EnbNext.Net7.Social;

namespace EnbNext.Net7.Tests;

public sealed class Net7SocialWireTests
{
    [Fact]
    public void GroupActionAndCtaResponseUseExactCurrentLayouts()
    {
        byte[] invite = new byte[3 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(invite.AsSpan(0, 2), 4);
        invite[2] = 1;
        Encoding.Latin1.GetBytes("Ron\0").CopyTo(invite, 3);
        Net7GroupActionUpdate action = Net7SocialWire.DecodeGroupAction(invite);
        Assert.Equal((byte)1, action.Type);
        Assert.Equal("Ron", action.InviteText);

        Net7CtaResponseUpdate cta = Net7SocialWire.DecodeCtaResponse(Convert.FromHexString("443322110F0000007F"));
        Assert.Equal(0x11223344u, cta.AvatarId);
        Assert.Equal(15u, cta.RequestType);
        Assert.Equal((byte)0x7F, cta.Success);

        // Safety rule: an unterminated invite keeps only the available bytes.
        Assert.Equal("Ron", Net7SocialWire.DecodeGroupAction(invite[..^1]).InviteText);
        Assert.Equal((byte)0, Net7SocialWire.DecodeCtaResponse(new byte[8]).Success);
    }

    [Fact]
    public void FindMemberUsesLittleEndianCountAndFourBigEndianDwords()
    {
        byte[] payload = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), 0x11223344);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), 0x55667788);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12, 4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16, 4), 1);
        Net7FindMemberRecord row = Assert.Single(Net7SocialWire.DecodeFindMember(payload));
        Assert.Equal(0x11223344u, row.ObjectId);
        Assert.Equal(0x55667788u, row.NumericField);
        Assert.Equal(2u, row.Race);
        Assert.Equal(1u, row.Profession);
        Assert.True(row.HasValidClassIndex);
        // 00886710: a record past the packet is an uninitialized allocation
        // (safety rule); complete records before it are kept.
        Assert.Empty(Net7SocialWire.DecodeFindMember(payload[..^1]));
        byte[] two = [.. payload, .. payload[4..^1]];
        BinaryPrimitives.WriteUInt32LittleEndian(two.AsSpan(0, 4), 2);
        Assert.Single(Net7SocialWire.DecodeFindMember(two));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonpositiveFinderCountProducesEmptyResponse(int count)
    {
        byte[] packet = new byte[7];
        BinaryPrimitives.WriteInt32LittleEndian(packet, count);
        Assert.Empty(Net7SocialWire.DecodeFindMember(packet));
    }

    [Fact]
    public void FinderTailStillUpdatesVisibleList()
    {
        byte[] packet = new byte[23];
        BinaryPrimitives.WriteInt32LittleEndian(packet, 1);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), 42);
        packet[20] = 255;
        var state = new Net7SocialState(id => "Pilot", () => 41);
        state.SetGroupFinderWindow(true);
        state.ApplyFindMember(Net7SocialWire.DecodeFindMember(packet));
        Assert.Equal(42u, Assert.Single(state.GroupFinderRows).ObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet, -1);
        state.ApplyFindMember(Net7SocialWire.DecodeFindMember(packet));
        Assert.Empty(state.GroupFinderRows);
    }

    [Fact]
    public void MessageChatstreamValidatesChatSizeAndTerminalPlayerNlpRecord()
    {
        byte[] nlp = PlayerNlpText("Hello");
        byte[] payload = new byte[7 + nlp.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), checked((ushort)nlp.Length));
        payload[2] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(3, 4), 0x11223344);
        nlp.CopyTo(payload, 7);
        Net7MessageChatstreamUpdate decoded = Net7SocialWire.DecodeMessageChatstream(payload);
        Assert.Equal((byte)3, decoded.Type);
        Assert.Equal(0x11223344u, decoded.ShipId);
        Assert.Equal("Hello", decoded.Text);

        byte[] wrongSize = payload.ToArray();
        wrongSize[0]++;
        Assert.Throws<InvalidDataException>(() => Net7SocialWire.DecodeMessageChatstream(wrongSize));
        byte[] signedNegativeEquivalent = new byte[7 + 0x8000];
        BinaryPrimitives.WriteUInt16LittleEndian(signedNegativeEquivalent.AsSpan(0, 2), 0x8000);
        Assert.Throws<InvalidDataException>(() => Net7SocialWire.DecodeMessageChatstream(signedNegativeEquivalent));
        byte[] badTerminal = payload.ToArray();
        badTerminal[^1] = 1;
        Assert.Equal("Hello", Net7SocialWire.DecodeMessageChatstream(badTerminal).Text);
    }

    [Fact]
    public void ChatstreamStopsAtFirstNulAndTerminalTypeIgnoringUnusedBytes()
    {
        byte[] nlp = [1, 0, 4, 0, (byte)'A', 0, 255, 254, 8, 255, 255, 127, 99];
        byte[] packet = new byte[7 + nlp.Length + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)nlp.Length);
        packet[2] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3), 42);
        nlp.CopyTo(packet, 7);
        var update = Net7SocialWire.DecodeMessageChatstream(packet);
        Assert.Equal("A", update.Text);
        Assert.Single(update.Segments);
        var state = new Net7SocialState(id => "Peer", () => 41, resolveAvatar: _ => 1);
        state.ApplyMessageChatstream(update);
        Assert.Equal("Peer (private): A", state.Messages[^1].Text);
    }

    [Fact]
    public void ClientChatListDecodesFieldsWithoutConsumerRestrictions()
    {
        for (uint kind = 0; kind <= 4; kind++)
        {
            string[] a = kind == 0 ? ["Peer"] : ["One", "Two"];
            string[] b = kind == 0 ? ["offline"] : Array.Empty<string>();
            Net7ClientChatListUpdate update = Net7SocialWire.DecodeClientChatList(ChatList(kind, "ctx", a, b));
            Assert.Equal(kind, update.ListKind);
            Assert.Equal(a, update.ArrayA.ToArray());
            Assert.Equal(b, update.ArrayB.ToArray());
        }
        Assert.Single(Net7SocialWire.DecodeClientChatList(ChatList(0, "", ["Peer"], [])).ArrayA);
        Assert.Equal(uint.MaxValue, Net7SocialWire.DecodeClientChatList(ChatList(uint.MaxValue, "", [], [])).ListKind);
        // 00879850 initializes ListKind to -1 and both counts to zero before
        // bounded reads: short packets are defined (kind 0 clears friends).
        var kindOnly = Net7SocialWire.DecodeClientChatList(new byte[] { 0, 0, 0, 0 });
        Assert.Equal(0u, kindOnly.ListKind);
        Assert.Empty(kindOnly.ArrayA); Assert.Empty(kindOnly.ArrayB);
        Assert.Equal(0xFFFFFF00u, Net7SocialWire.DecodeClientChatList(new byte[] { 0 }).ListKind);
        Assert.Equal(uint.MaxValue, Net7SocialWire.DecodeClientChatList(ReadOnlySpan<byte>.Empty).ListKind);
    }

    [Fact]
    public void SharedStringTerminatorsPreserveFollowingFieldsAndVisibleChatState()
    {
        var state = new Net7SocialState(_ => null, () => 9);
        state.SetFriendsWindow(true);
        state.ApplyClientChatList(Net7SocialWire.DecodeClientChatList(
            ChatList(0, "Context\0unused", ["Peer\0unused"], ["Saturn\0unused"])));
        Assert.Equal("Peer", Assert.Single(state.Friends).Key);
        byte[] message = ChatEvent(4, 0, ["route\0unused", "Display\0unused", "", "", "Body\0unused", ""], []);
        state.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(message));
        Assert.Equal("route", state.ReplyIdentity);
        Assert.Contains("Body", state.Messages[^1].Text);
        Assert.DoesNotContain("unused", state.Messages[^1].Text);
        state.ApplyClientChatError(Net7SocialWire.DecodeClientChatError(
            ChatError(4, 1, "Target\0unused", "", "")));
        Assert.Equal("SYSTEM: Target is not online at this time.", state.Messages[^1].Text);
    }
    [Fact]
    public void InitializedChatPrefixesStillReachTheirConsumers()
    {
        var state = new Net7SocialState(_ => null, () => 9);
        byte[] login = ChatEvent(1, 0, ["route", "Pilot", "", "", "", ""], []);
        // End immediately after the second string, before the four empty strings.
        state.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(login[..^12]));
        Assert.Contains(state.Messages, message => message.Text.Contains("Pilot"));
        byte[] error = ChatError(4, 1, "Target", "", "");
        state.ApplyClientChatError(Net7SocialWire.DecodeClientChatError(error[..^4]));
        Assert.Equal("SYSTEM: Target is not online at this time.", state.Messages[^1].Text);
        var empty = Net7SocialWire.DecodeClientChatError([]);
        Assert.Equal(1u, empty.ErrorOrStatusCode);
        Assert.Equal(uint.MaxValue, empty.RequestCode);
        Assert.Equal("", empty.Ss1);
        Assert.Equal(0u, Net7SocialWire.DecodeClientChatEvent([]).EventType);
        Assert.Equal(0xffffff02u, Net7SocialWire.DecodeClientChatError([1, 0, 0, 0, 2]).RequestCode);
    }

    [Fact]
    public void ClientChatEventAndErrorPreserveEverySharedStringAndBlob()
    {
        byte[] eventPayload = ChatEvent(4, 9, ["route", "display", "s3", "chan`x", "body", "s6"], [0xAA, 0xBB]);
        Net7ClientChatEventUpdate e = Net7SocialWire.DecodeClientChatEvent(eventPayload);
        Assert.Equal(4u, e.EventType);
        Assert.Equal("route", e.Ss1);
        Assert.Equal("display", e.Ss2);
        Assert.Equal("body", e.Ss5);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, e.Blob);

        Net7ClientChatErrorUpdate error = Net7SocialWire.DecodeClientChatError(ChatError(4, 1, "Peer", "x", "y"));
        Assert.Equal(4u, error.ErrorOrStatusCode);
        Assert.Equal(1u, error.RequestCode);
        Assert.Equal("Peer", error.Ss1);
        Assert.Throws<InvalidDataException>(() => Net7SocialWire.DecodeClientChatEvent(eventPayload[..^1]));
    }

    [Fact]
    public void ClientChatListKeepsTheDecodedPrefixWhenAStringOrCountRunsPastThePacket()
    {
        // Safety rule over 0086EB50/004AACE0: undefined bytes past the packet are
        // not read; available string bytes and earlier entries are kept.
        byte[] list = ChatList(0, "ctx", ["Peer", "Other"], []);
        var update = Net7SocialWire.DecodeClientChatList(list[..^6]);
        Assert.Equal(["Peer", "Oth"], update.ArrayA.ToArray());
        Assert.Empty(update.ArrayB);
        byte[] overDeclared = ChatList(0, "ctx", ["Peer"], []);
        int countOffset = 4 + 2 + 3;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(overDeclared.AsSpan(countOffset), 0xFFFFFFFF);
        var over = Net7SocialWire.DecodeClientChatList(overDeclared[..^4]);
        Assert.Equal(["Peer"], over.ArrayA.ToArray());
    }

    [Fact]
    public void PlayerNlpKeepsRecordsDecodedBeforeTheStreamEndsAndRunsTextOnLikeTheCString()
    {
        // 004C48C0 walks records unbounded and appends Type-1 text as a C string.
        byte[] stream = PlayerNlpText("Hi");
        var truncatedTerminal = Net7SocialWire.DecodePlayerNlp(stream[..^2]);
        Assert.Equal("Hi", Assert.Single(truncatedTerminal).Text);
        Assert.Empty(Net7SocialWire.DecodePlayerNlp(new byte[] { 1, 0 }));
        byte[] unterminated = [1, 0, 2, 0, (byte)'H', (byte)'i', 8, 0, 0, 0];
        var runOn = Net7SocialWire.DecodePlayerNlp(unterminated);
        Assert.Equal("Hi\b", Assert.Single(runOn).Text);
        byte[] pastEnd = [1, 0, 9, 0, (byte)'H', (byte)'i'];
        Assert.Equal("Hi", Assert.Single(Net7SocialWire.DecodePlayerNlp(pastEnd)).Text);
    }

    [Fact]
    public void SocialPacketUnusedTailsDoNotSuppressListMessageOrErrorUpdates()
    {
        var state = new Net7SocialState(_ => null, () => 9);
        byte[] list = ChatList(0, "", ["Peer"], ["Saturn"]);
        state.ApplyClientChatList(Net7SocialWire.DecodeClientChatList((byte[])[.. list, 0xFF, 0x80]));
        Assert.Contains(state.Messages, x => x.Text == "Peer");

        byte[] message = ChatEvent(4, 0, ["route", "Display", "", "", "Body", ""], []);
        state.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent((byte[])[.. message, 0xFF, 0x80]));
        Assert.Equal("route", state.ReplyIdentity);
        Assert.Contains("Body", state.Messages[^1].Text);

        byte[] error = ChatError(4, 1, "Target", "", "");
        state.ApplyClientChatError(Net7SocialWire.DecodeClientChatError((byte[])[.. error, 0xFF, 0x80]));
        Assert.Equal("SYSTEM: Target is not online at this time.", state.Messages[^1].Text);
        Assert.Equal("route", state.ReplyIdentity);

        byte[] blobMessage = ChatEvent(4, 9, ["a", "b", "c", "d", "e", "f"], [1, 2]);
        Assert.Equal(new byte[] { 1, 2 }, Net7SocialWire.DecodeClientChatEvent((byte[])[.. blobMessage, 3, 4]).Blob);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnequalFriendVectorsPreserveListAndRespectClosedWindowFallback(bool open)
    {
        var state = new Net7SocialState(_ => null, () => 9);
        state.SetFriendsWindow(open);
        state.ApplyClientChatList(Net7SocialWire.DecodeClientChatList(ChatList(0, "", ["Existing"], ["Saturn"])));
        var revision = state.FriendsRevision;
        int messages = state.Messages.Count;
        state.ApplyClientChatList(Net7SocialWire.DecodeClientChatList(ChatList(0, "", ["New"], [])));
        Assert.Equal(revision, state.FriendsRevision);
        if (open) Assert.Equal(messages, state.Messages.Count);
        else Assert.Equal("New", state.Messages[^1].Text);
    }

    [Theory]
    [InlineData(4u)]
    [InlineData(5u)]
    [InlineData(uint.MaxValue)]
    public void UndispatchedChatListKindsLeaveSocialStateUnchanged(uint kind)
    {
        var state = new Net7SocialState(_ => null, () => 9);
        var revision = state.ChatRevision;
        state.ApplyClientChatList(Net7SocialWire.DecodeClientChatList(ChatList(kind, "", ["Ignored"], [])));
        Assert.Equal(revision, state.ChatRevision);
        Assert.Empty(state.Messages);
        Assert.Empty(state.CurrentChannels);
    }

    [Fact]
    public void InviteUsesFirstNulWithinDeclaredDataAndIgnoresPacketTail()
    {
        byte[] packet = [6, 0, 1, (byte)'P', (byte)'e', (byte)'e', (byte)'r', 0, 0xFF, 0x80];
        var state = new Net7SocialState(_ => null, () => 9);
        state.ApplyGroupAction(Net7SocialWire.DecodeGroupAction(packet));
        Assert.Equal("Peer", state.GroupInvite!.InviteText);
        Assert.Equal(6, state.GroupInvite.Data.Length);
    }

    [Fact]
    public void ShortGroupDataWithTerminatedInviteStillOpensInvite()
    {
        var state = new Net7SocialState(_ => null, () => 9);
        state.ApplyGroupAction(Net7SocialWire.DecodeGroupAction([0xff, 0x7f, 1, (byte)'P', 0]));
        Assert.Equal("P", state.GroupInvite!.InviteText);
        Assert.Equal(1, state.GroupInviteRevision);
        state.ApplyGroupAction(Net7SocialWire.DecodeGroupAction([0xff, 0x7f, 2]));
        Assert.Equal("P", state.GroupInvite.InviteText);
        Assert.Equal(1, state.GroupInviteRevision);
    }

    [Fact]
    public void CtaMissingStatusStillChangesFormationAndPartialIdsKeepDefaults()
    {
        Assert.Equal(new Net7CtaResponseUpdate(uint.MaxValue, uint.MaxValue, 0),
            Net7SocialWire.DecodeCtaResponse([]));
        Assert.Equal(new Net7CtaResponseUpdate(0xffffff34, uint.MaxValue, 0),
            Net7SocialWire.DecodeCtaResponse([0x34]));
        Assert.Equal(new Net7CtaResponseUpdate(1, 0xffffff0f, 0),
            Net7SocialWire.DecodeCtaResponse([1, 0, 0, 0, 15]));
        var state = new Net7SocialState(_ => null, () => 9);
        state.ApplyCtaResponse(Net7SocialWire.DecodeCtaResponse([1, 0, 0, 0, 15, 0, 0, 0]));
        Assert.True(state.GroupFormationVisualEnabled);
        state.ApplyCtaResponse(Net7SocialWire.DecodeCtaResponse([1, 0, 0, 0, 14, 0, 0, 0]));
        Assert.False(state.GroupFormationVisualEnabled);
    }

    [Fact]
    public void CtaUnusedTailDoesNotSuppressFormationUpdate()
    {
        var state = new Net7SocialState(_ => null, () => 9);
        state.ApplyCtaResponse(Net7SocialWire.DecodeCtaResponse(Convert.FromHexString("443322110F00000000FF80")));
        Assert.True(state.GroupFormationVisualEnabled);
        state.ApplyCtaResponse(Net7SocialWire.DecodeCtaResponse(Convert.FromHexString("443322110E00000001FF80")));
        Assert.False(state.GroupFormationVisualEnabled);
    }

    internal static byte[] ChatList(uint kind, string context, IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        using var ms = new MemoryStream();
        U32(ms, kind); Shared(ms, context); U32(ms, checked((uint)a.Count)); foreach (string x in a) Shared(ms, x);
        U32(ms, checked((uint)b.Count)); foreach (string x in b) Shared(ms, x);
        return ms.ToArray();
    }

    internal static byte[] ChatEvent(uint type, uint field, IReadOnlyList<string> strings, byte[]? blob = null)
    {
        Assert.Equal(6, strings.Count);
        using var ms = new MemoryStream();
        U32(ms, type); U32(ms, field); foreach (string x in strings) Shared(ms, x);
        byte[] value = blob ?? Array.Empty<byte>(); U32(ms, checked((uint)value.Length)); ms.Write(value);
        return ms.ToArray();
    }

    internal static byte[] ChatError(uint status, uint request, string s1, string s2, string s3)
    {
        using var ms = new MemoryStream(); U32(ms, status); U32(ms, request); Shared(ms, s1); Shared(ms, s2); Shared(ms, s3); return ms.ToArray();
    }

    internal static byte[] PlayerNlpText(string text)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(text + "\0");
        byte[] result = new byte[4 + bytes.Length + 4];
        result[0] = 1; result[1] = 0; BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2, 2), checked((ushort)bytes.Length));
        bytes.CopyTo(result, 4); result[^4] = 8; return result;
    }

    private static void Shared(Stream s, string value)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(value); Span<byte> len = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(len, checked((ushort)bytes.Length)); s.Write(len); s.Write(bytes);
    }
    private static void U32(Stream s, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); s.Write(bytes); }
}
