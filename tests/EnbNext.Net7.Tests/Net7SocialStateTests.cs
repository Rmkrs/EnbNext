using System.Buffers.Binary;
using System.Text;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Social;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7SocialStateTests
{
    [Theory]
    [InlineData(132u)]
    [InlineData(0x7fffffffu)]
    [InlineData(0x80000000u)]
    [InlineData(0xffffffffu)]
    public void UndefinedGuildMessagePreservesPresentationAndAcceptsFollowingMessage(uint type)
    {
        var state = new Net7SocialState();
        state.ApplyGuildMessage(new(6, "Before", "Guild"));
        var presentation = state.GuildPresentation;
        var messages = state.Messages.ToArray();
        var events = state.PresentationEvents.ToArray();
        long revision = state.ChatRevision;
        state.ApplyGuildMessage(new(type, "Undefined", "Guild"));
        Assert.Same(presentation, state.GuildPresentation);
        Assert.Equal(messages, state.Messages);
        Assert.Equal(events, state.PresentationEvents);
        Assert.Equal(revision, state.ChatRevision);
        state.ApplyGuildMessage(new(6, "After", "Guild"));
        Assert.Equal("Guild MOTD : After", state.Messages[^1].Text);
        Assert.Equal(messages.Length + 1, state.Messages.Count);
        Assert.Equal(events.Length + 1, state.PresentationEvents.Count);
    }

    [Theory]
    [InlineData(21u, "", "now")]
    [InlineData(21u, "offline", "now")]
    [InlineData(22u, "", "no longer")]
    [InlineData(22u, "elsewhere", "no longer")]
    public void FriendshipMessageUsesActorAndFriendRatherThanListStatus(uint kind, string status, string verb)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.Latin1, true);
        writer.Write(kind); writer.Write(0u);
        foreach (string field in new[] { "routing-id", "Actor", "unused", "", "Friend", status })
        { byte[] text = Encoding.Latin1.GetBytes(field); writer.Write((ushort)text.Length); writer.Write(text); }
        writer.Write(0u);
        var state = new Net7SocialState();
        state.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(bytes.ToArray()));
        Assert.Equal($"COMPUTER: Actor is {verb} friends with Friend.", state.Messages[^1].Text);
        if (kind == 21) Assert.Equal(status, state.Friends["Friend"].Presence);
        else Assert.Empty(state.Friends);
    }

    [Theory]
    [InlineData(12u, Net7ChatPresentationKind.PrivateChannelPasswordRequired, false)]
    [InlineData(13u, Net7ChatPresentationKind.PrivateChannelPasswordIncorrect, true)]
    public void ProtectedChannelResponsesSelectPasswordModeAndExactCue(uint status, Net7ChatPresentationKind kind, bool sounds)
    {
        var state = new Net7SocialState();
        state.ApplyClientChatError(new(status, 6, "", "42", ""));
        Assert.Single(state.PresentationEvents, e => e.Kind == kind);
        Assert.Equal(sounds, state.PresentationEvents.Any(e => e.Kind == Net7ChatPresentationKind.InterfaceSound && e.Text == "cost.wav"));
        Assert.DoesNotContain(state.PresentationEvents, e => e.Kind == Net7ChatPresentationKind.PrivateChannelNotFound);
        Assert.DoesNotContain(state.PresentationEvents, e => e.Kind == Net7ChatPresentationKind.Error);
        Assert.Empty(state.Messages);
        var other = new Net7SocialState();
        other.ApplyClientChatError(new(status, 22, "", "42", ""));
        Assert.DoesNotContain(other.PresentationEvents, e => e.Kind == kind);
    }

    [Fact]
    public void BannedJoinReturnsToOrdinaryChannelEntryWithCostAndMessage()
    {
        var state = new Net7SocialState();
        state.ApplyClientChatError(new(10, 6, "", "42", ""));
        Assert.Contains(state.PresentationEvents, e => e.Kind == Net7ChatPresentationKind.PrivateChannelJoinEntry);
        Assert.DoesNotContain(state.PresentationEvents, e => e.Kind == Net7ChatPresentationKind.PrivateChannelNotFound);
        Assert.Contains(state.PresentationEvents, e => e.Kind == Net7ChatPresentationKind.InterfaceSound && e.Text == "cost.wav");
        Assert.Contains(state.Messages, m => m.Text == "SYSTEM: You have been banned from entering channel: 42");
    }

    [Theory]
    [InlineData(0u, 6u, "42", false)]
    [InlineData(2u, 6u, "42", true)]
    [InlineData(2u, 6u, "Sector123", false)]
    [InlineData(12u, 6u, "Sector123", true)]
    [InlineData(13u, 6u, "42", true)]
    [InlineData(10u, 6u, "42", true)]
    [InlineData(2u, 4u, "42", false)]
    [InlineData(2u, 7u, "42", false)]
    public void PrivateRequestCompletionFollowsRetailErrorConsumer(uint status, uint request, string channel, bool completes)
    {
        var state = new Net7SocialState();
        state.ApplyClientChatError(new(status, request, "", channel, ""));
        Assert.Equal(completes, state.PresentationEvents.Any(e => e.Kind == Net7ChatPresentationKind.PrivateChannelRequestCompleted));
    }

    [Theory]
    [InlineData(7u, 0u, false)]
    [InlineData(7u, 1u, true)]
    [InlineData(8u, 1u, false)]
    [InlineData(9u, 0u, true)]
    public void PrivateRequestCompletionRequiresSelfJoinOrCreation(uint kind, uint self, bool completes)
    {
        var state = new Net7SocialState();
        state.ApplyClientChatEvent(new(kind, self, "Self", "", "", "42", "", "", []));
        Assert.Equal(completes, state.PresentationEvents.Any(e => e.Kind == Net7ChatPresentationKind.PrivateChannelRequestCompleted));
    }

    [Fact]
    public void GuildRosterUsesCaseSensitiveOrderWithinOnlineAndOfflineGroups()
    {
        var state=new Net7SocialState {GuildWindowActive=true};
        state.ApplyGuildMessage(new(3,"alpha \"Member\"\nZulu \"Officer\"\nBeta \"Member\"\nAardvark \"Member\" (offline)",""));
        Assert.Equal(new[]{"Beta","Zulu","alpha","Aardvark"},state.GuildRosterEntries.Select(e=>e.Identity));
        Assert.Equal("\"Member\" (offline)",state.GuildRosterEntries[^1].Status);
    }

    [Fact]
    public void ClientChatListKindAboveThreeChangesNothing()
    {
        // 0073CC90 switches on kinds 0..3 only; kind 4 and above fall out.
        var state = new Net7SocialState();
        state.ApplyClientChatList(new(0, "", ["Peer"], ["offline"]));
        long chat = state.ChatRevision, friends = state.FriendsRevision;
        int messages = state.Messages.Count;
        state.ApplyClientChatList(new(4, "", ["Other"], ["online"]));
        state.ApplyClientChatList(new(9, "", [], []));
        Assert.Equal(chat, state.ChatRevision);
        Assert.Equal(friends, state.FriendsRevision);
        Assert.Equal("Peer", Assert.Single(state.Friends).Key);
        Assert.Equal(messages, state.Messages.Count);
    }

    [Fact]
    public void FriendsRefreshRevisionDistinguishesListRebuildFromOrdinaryChat()
    {
        var state = new Net7SocialState();
        state.ApplyClientChatList(new(0, "", ["Peer"], ["offline"]));
        long revision = state.FriendsRevision;
        state.ApplyBaseMessage(new(0, "ordinary chat"));
        Assert.Equal(revision, state.FriendsRevision);
        state.ApplyClientChatList(new(0, "", ["Peer"], ["offline"]));
        Assert.Equal(++revision, state.FriendsRevision);
        state.ApplyClientChatEvent(new(21, 0, "", "", "", "", "Peer", "offline", []));
        Assert.Equal(++revision, state.FriendsRevision);
        state.ApplyClientChatEvent(new(22, 0, "", "", "", "", "Missing", "offline", []));
        Assert.Equal(++revision, state.FriendsRevision);
        Assert.Single(state.Friends);
        Assert.Equal(revision, EnbNext.Presentation.EnbSceneChatSnapshot.From(state).FriendsRevision);
    }

    [Fact]
    public void PrivateChannelResponsesPublishRetailCuesAndRetryDialog()
    {
        var state = new Net7SocialState();
        state.ApplyClientChatEvent(new(9, 0, "", "", "", "100", "", "", []));
        Assert.Contains(state.PresentationEvents, e => e.Kind == Net7ChatPresentationKind.InterfaceSound && e.Text == "success_1.wav");
        int before = state.PresentationEvents.Count;
        state.ApplyClientChatEvent(new(8, 0, "Peer", "Peer", "", "100", "", "", []));
        Assert.Equal(before, state.PresentationEvents.Count);
        state.ApplyClientChatEvent(new(8, 1, "Self", "Self", "", "100", "", "", []));
        Assert.Equal(before + 1, state.PresentationEvents.Count);
        Assert.Empty(state.PrivateChannel);
        state.ApplyClientChatError(new(2, 6, "", "100", ""));
        Assert.Contains(state.PresentationEvents, e => e.Kind == Net7ChatPresentationKind.PrivateChannelNotFound);
        Assert.Contains(state.PresentationEvents, e => e.Kind == Net7ChatPresentationKind.InterfaceSound && e.Text == "cost.wav");
        Assert.Contains(state.Messages, m => m.Text.Contains("Could not enter channel 100"));
    }

    [Theory]
    [InlineData(0u, 6u, "100")]
    [InlineData(2u, 6u, "Sector123")]
    [InlineData(2u, 7u, "100")]
    [InlineData(10u, 6u, "100")]
    [InlineData(12u, 6u, "100")]
    [InlineData(13u, 6u, "100")]
    public void UnrelatedChannelResponsesDoNotOpenNotFound(uint status, uint request, string channel)
    {
        var state = new Net7SocialState();
        state.ApplyClientChatError(new(status, request, "", channel, ""));
        Assert.DoesNotContain(state.PresentationEvents, e => e.Kind == Net7ChatPresentationKind.PrivateChannelNotFound);
    }

    [Fact]
    public void GuildMotdPublishesAuthoredSoundAlongsideHistory()
    {
        var state = new Net7SocialState();
        state.ApplyGuildMessage(new Net7GuildMessage(6, "Welcome", "Guild"));
        var sound = Assert.Single(state.PresentationEvents);
        Assert.Equal(Net7ChatPresentationKind.GuildSound, sound.Kind);
        Assert.Equal("Guild_Announcement", sound.Text);
        state.ApplyGuildMessage(new Net7GuildMessage(121, "1", "Guild"));
        Assert.Single(state.PresentationEvents); // Authored null sound stays silent.
        state.ApplyGuildMessage(new Net7GuildMessage(6, "Welcome", "Guild"));
        Assert.Equal(2, state.PresentationEvents.Count);
        Assert.True(state.PresentationEvents[1].Sequence > sound.Sequence);
    }
    [Fact]
    public void GroupSymbolsAreDistinctEventsAndDoNotEnterChatHistory()
    {
        var state=new Net7SocialState();
        void Receive(string channel,string text)=>state.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(
            Net7SocialWireTests.ChatEvent(3,0,["route","Display","",channel,text,""],[])));
        Receive("Group`123","<S>0Kestrabel`G_READY");
        Receive("Group`123","<S>0Kestrabel`G_READY");
        Receive("Group`123","<S>1Kestrabel`G_TARGET");
        Assert.Empty(state.Messages);
        Assert.Equal(3,state.GroupSymbols.Count);
        Assert.Equal(new long[]{1,2,3},state.GroupSymbols.Select(s=>s.Sequence));
        Assert.Equal("Kestrabel",state.GroupSymbols[0].Member);
        Assert.Equal("G_READY",state.GroupSymbols[0].Asset);
        Assert.True(state.GroupSymbols[2].Context);
        Receive("Group","Ready when you are");
        Receive("General","<S>0Kestrabel`G_READY");
        Receive("Group","<S>2Kestrabel`G_READY");
        Receive("Group","<S>0missing-separator");
        Assert.Equal(4,state.Messages.Count);
        Assert.Equal(3,state.GroupSymbols.Count);
    }

    [Fact]
    public void BaseMessagePresentationNormalizesPrefixesBoundsHistoryAndKeepsAttentionOut()
    {
        var social = new Net7SocialState();
        social.ApplyBaseMessage(new Net7MessageState(0, "ctx"));
        social.ApplyBaseMessage(new Net7MessageState(6, "tip"));
        social.ApplyBaseMessage(new Net7MessageState(21, "warn"));
        social.ApplyBaseMessage(new Net7MessageState(22, "attention"));
        Assert.Equal("COMPUTER: ctx", social.Messages[0].Text);
        Assert.Equal("TIP: tip", social.Messages[1].Text);
        Assert.Equal("WARNING: warn", social.Messages[2].Text);
        Assert.DoesNotContain(social.Messages, x => x.Text.Contains("attention", StringComparison.Ordinal));
        Assert.Contains(social.PresentationEvents, x => x.Kind == Net7ChatPresentationKind.QuickLine);

        int messageCount = social.Messages.Count;
        int presentationCount = social.PresentationEvents.Count;
        social.ApplyBaseMessage(new Net7MessageState(7, string.Empty));
        social.ApplyBaseMessage(new Net7MessageState(0, string.Empty));
        Assert.Equal(messageCount, social.Messages.Count);
        Assert.Equal(presentationCount, social.PresentationEvents.Count);

        for (int i = 0; i < 210; i++) social.ApplyBaseMessage(new Net7MessageState(7, $"m{i}"));
        Assert.Equal(Net7SocialState.ChatHistoryCapacity, social.Messages.Count);
        Assert.Equal("m10", social.Messages[0].Text);
        Assert.Throws<InvalidDataException>(() => social.ApplyBaseMessage(new Net7MessageState(24, "bad")));
    }

    [Fact]
    public void PrivateIngressKeepsRoutingDisplayReplyAndNamedChannelIdentityDistinct()
    {
        var state = new Net7SocialState(id => id == 7 ? "ShipPeer" : null, () => 99, resolveAvatar: id => id == 7 ? 1UL : null);
        state.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(4, 0, ["route", "Display", "", "", "Body", ""], [])));
        Assert.Equal("route", state.ReplyIdentity);
        Net7MessageState tell = Assert.Single(state.Messages);
        Assert.Equal("Display (private): Body", tell.Text);
        Assert.Equal("route", tell.RoutingIdentity);
        Assert.Equal("Display", tell.DisplayIdentity);
        Assert.Contains(state.PresentationEvents, x => x.Kind == Net7ChatPresentationKind.PrivateBeep && x.Text == "route");

        state.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(3, 0, ["speaker", "Visible", "", "Raid`private", "Text", ""], [])));
        Assert.Equal("route", state.ReplyIdentity);
        Assert.Equal("[Raid] Visible: Text", state.Messages[^1].Text);

        byte[] nlp = Net7SocialWireTests.PlayerNlpText("NLP");
        byte[] packet = new byte[7 + nlp.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), checked((ushort)nlp.Length)); packet[2] = 3; BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3, 4), 7); nlp.CopyTo(packet, 7);
        state.ApplyMessageChatstream(Net7SocialWire.DecodeMessageChatstream(packet));
        Assert.Equal("ShipPeer", state.ReplyIdentity);
        Assert.Equal("ShipPeer (private): NLP", state.Messages[^1].Text);
    }

    [Fact]
    public void ChatstreamWithoutAvatarHasNoEffectsAndNextCachedAvatarStreamWorks()
    {
        bool cached = false;
        var state = new Net7SocialState(_ => "Peer", () => 9,
            resolveAvatarCache: _ => cached ? 1UL : null);
        var update = Chatstream(5, 3, "Hello");
        state.ApplyMessageChatstream(update);
        Assert.Empty(state.Messages);
        Assert.Empty(state.AvatarEmotes);
        Assert.Empty(state.AvatarSpeech);
        Assert.Empty(state.PresentationEvents);
        Assert.Null(state.ReplyIdentity);
        cached = true;
        state.ApplyMessageChatstream(update);
        Assert.Equal("Peer (private): Hello", Assert.Single(state.Messages).Text);
        Assert.Single(state.AvatarEmotes);
        Assert.False(Assert.Single(state.AvatarSpeech).WorldObjectPresent);
    }

    [Fact]
    public void GroupStreamSuppressesOwnTextButKeepsRemoteEmptyTextAndNotification()
    {
        var state = new Net7SocialState(id => id == 9 ? "Self" : "Peer", () => 9,
            resolveAvatar: _ => 1);
        state.ApplyMessageChatstream(Chatstream(9, 4, "Self"));
        Assert.Empty(state.Messages);
        Assert.Empty(state.PresentationEvents);
        Assert.Single(state.AvatarSpeech);

        state.ApplyMessageChatstream(Chatstream(5, 4, ""));
        Assert.Equal("Peer (group): ", Assert.Single(state.Messages).Text);
        Assert.Contains(state.PresentationEvents, x => x.Kind == Net7ChatPresentationKind.PrivateBeep);
        Assert.Null(state.ReplyIdentity);

        state.ApplyMessageChatstream(Chatstream(5, 3, ""));
        Assert.Equal("Peer (private): ", state.Messages[^1].Text);
        Assert.Equal("Peer", state.ReplyIdentity);
    }

    [Fact]
    public void Private005FSuppressesSelfAndUnknownAndA6IsAsynchronousWithoutReplyMutation()
    {
        var state = new Net7SocialState(id => id == 5 ? "Peer" : null, () => 9);
        state.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(4, 0, ["reply", "R", "", "", "M", ""], [])));
        Assert.Equal("reply", state.ReplyIdentity);
        int before = state.Messages.Count;
        state.ApplyMessageChatstream(Chatstream(9, 3, "Self"));
        state.ApplyMessageChatstream(Chatstream(100, 3, "Unknown"));
        Assert.Equal(before, state.Messages.Count);

        state.ApplyClientChatError(Net7SocialWire.DecodeClientChatError(Net7SocialWireTests.ChatError(4, 1, "Target", "", "")));
        Assert.Equal("reply", state.ReplyIdentity);
        Assert.Equal("SYSTEM: Target is not online at this time.", state.Messages[^1].Text);
        state.ApplyClientChatError(Net7SocialWire.DecodeClientChatError(Net7SocialWireTests.ChatError(14, 1, "Target", "", "")));
        Assert.Equal("SYSTEM: Could not broadcast to Target. (no permission)", state.Messages[^1].Text);
        int count = state.Messages.Count;
        state.ApplyClientChatError(Net7SocialWire.DecodeClientChatError(Net7SocialWireTests.ChatError(0, 1, "Target", "", "")));
        Assert.Equal(count, state.Messages.Count);
    }


    [Fact]
    public void GroupInviteUnusedTypesAndCtaUnsupportedTypeStayBounded()
    {
        var state = new Net7SocialState();
        state.ApplyGroupAction(new Net7GroupActionUpdate(2, new byte[] { 0xAA }));
        Assert.Null(state.GroupInvite);
        state.ApplyGroupAction(new Net7GroupActionUpdate(1, new byte[] { (byte)'P', 0 }) { InviteText = "P" });
        Assert.Equal("P", state.GroupInvite!.InviteText);

        foreach (uint type in new uint[] { 7, 16, 0, uint.MaxValue })
        {
            state.ApplyCtaResponse(new Net7CtaResponseUpdate(1, type, 1));
            Assert.Equal(type, state.LastUnhandledCtaResponse!.RequestType);
            Assert.False(state.GroupFormationVisualEnabled);
            Assert.Empty(state.HighlightedGroupAvatarIds);
        }
    }

    [Fact]
    public void RichPrivateMessageWithEmptyNlpTextStillPublishesPacketTextAndReply()
    {
        var state = new Net7SocialState(_ => "Peer", () => 9,
            resolveAvatar: _ => 1UL, resolveCaller: _ => 10u);
        state.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(4, 0,
            ["Peer", "Visible", "", "", "Body", ""], Net7SocialWireTests.PlayerNlpText(""))));
        Assert.Equal("Visible (private): Body", Assert.Single(state.Messages).Text);
        Assert.Equal("Peer", state.ReplyIdentity);
        Assert.Single(state.AvatarSpeech);
        Assert.Contains(state.PresentationEvents, e => e.Kind == Net7ChatPresentationKind.PrivateBeep);
    }

    [Fact]
    public void RichA5TellSuppressesResolvedLocalAvatarButNotMatchingNameWithoutAvatar()
    {
        var local = new Net7SocialState(id => id == 9 ? "Self" : null, () => 9,
            resolveAvatar: id => id == 9 ? 1UL : null, resolveCaller: name => name == "Self" ? 9u : null);
        local.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(4, 0, ["Self", "Self", "", "", "Echo", ""], Net7SocialWireTests.PlayerNlpText("Echo"))));
        Assert.Empty(local.Messages);
        Assert.Null(local.ReplyIdentity);

        foreach (bool callerKnown in new[] { false, true })
        {
            var missingAvatar = new Net7SocialState(id => id == 9 ? "Self" : null, () => 9,
                resolveCaller: name => callerKnown && name == "Self" ? 9u : null);
            missingAvatar.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(4, 0, ["Self", "Self", "", "", "Echo", ""], [1])));
            Assert.Equal("Self (private): Echo", Assert.Single(missingAvatar.Messages).Text);
            Assert.Equal("Self", missingAvatar.ReplyIdentity);
            Assert.Contains(missingAvatar.PresentationEvents, e => e.Kind == Net7ChatPresentationKind.PrivateBeep);
        }

        var unresolved = new Net7SocialState(_ => null, () => 9);
        unresolved.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(4, 0, ["Remote", "Visible", "", "", "Body", ""], [1])));
        Assert.Equal("Visible (private): Body", Assert.Single(unresolved.Messages).Text);
        Assert.Equal("Remote", unresolved.ReplyIdentity);
    }

    [Fact]
    public void ChatstreamFromAnAvatarWithAnEmptyNameStillPrintsAndSetsTheReply()
    {
        // 00736F10 formats the resolved avatar's name without testing it.
        var social = new Net7SocialState(_ => "", () => 1, id => id == 2 ? 9ul : null);
        social.ApplyMessageChatstream(new(0, 3, 2, [new(1, 0, [], "Hi"), new(8, 0, [], null)]));
        Assert.Equal(" (private): Hi", Assert.Single(social.Messages).Text);
        Assert.Equal("", social.ReplyIdentity);
    }

    [Fact]
    public void RepeatedPrivatePeerSelectsItsSlotWithoutReorderingTheRecentList()
    {
        // 00565D80: a name already in the five recent slots only becomes the
        // reply target; a new name shifts the slots and enters slot 0.
        var state = new Net7SocialState();
        foreach (string peer in new[] { "Alpha", "Beta", "Alpha" })
            state.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(4, 0, [peer, peer, "", "", "Hi", ""], [])));
        Assert.Equal("Alpha", state.ReplyIdentity);
        Assert.Equal(new[] { "Beta", "Alpha" }, state.RecentPrivatePeers.ToArray());
    }

    [Fact]
    public void PrivatePeerMruAndSecureTargetsUseCaseSensitiveIdentityAndFriendActionPreservesUiGate()
    {
        var state = new Net7SocialState();
        state.PromoteSecureTarget("Peer");
        state.PromoteSecureTarget("peer");
        Assert.Equal(new[] { "peer", "Peer" }, state.SecureTargets.Take(2).ToArray());

        state.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(4, 0, ["Peer", "Visible", "", "", "One", ""], [])));
        state.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(4, 0, ["peer", "Visible", "", "", "Two", ""], [])));
        Assert.Equal("peer", state.ReplyIdentity);
        Assert.Equal(new[] { "peer", "Peer" }, state.RecentPrivatePeers.Take(2).ToArray());

        state.ApplyClientChatList(Net7SocialWire.DecodeClientChatList(Net7SocialWireTests.ChatList(0, "", ["OfflinePeer", "OnlinePeer"], ["OFFLINE", "Saturn"])));
        Net7PrivateFriendActionPlan offline = state.PlanPrivateChatWithFriend("OfflinePeer");
        Assert.False(offline.ShouldOpenPrivateUi);
        Assert.False(offline.PromoteSecureTarget);
        Assert.Equal("Friend not online.", offline.LocalError);

        Net7PrivateFriendActionPlan online = state.PlanPrivateChatWithFriend("OnlinePeer");
        Assert.True(online.ShouldOpenPrivateUi);
        Assert.True(online.PromoteSecureTarget);
        Assert.Equal("OnlinePeer", online.TargetIdentity);
        Assert.Null(online.LocalError);
    }

    [Fact]
    public void FriendsIgnorePresenceAndGroupFinderMaterializeCurrentModels()
    {
        var state = new Net7SocialState(id => id == 1 ? "Self" : id == 2 ? "Peer" : null, () => 1);
        state.ApplyClientChatList(Net7SocialWire.DecodeClientChatList(Net7SocialWireTests.ChatList(0, "", ["Peer", "Away"], ["OFFLINE", "Saturn"])));
        Assert.True(state.Friends["Peer"].IsOffline);
        Assert.False(state.CanStartPrivateChatWithFriend("Peer"));
        state.ApplyClientChatList(Net7SocialWire.DecodeClientChatList(Net7SocialWireTests.ChatList(1, "", ["Noisy"], [])));
        Assert.Contains("Noisy", state.Ignored);

        state.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(19, 0, ["", "", "", "", "Mute", "Unused"], [])));
        Assert.Contains("Mute", state.Ignored);
        state.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(20, 0, ["", "", "", "", "Mute", "Unused"], [])));
        Assert.DoesNotContain("Mute", state.Ignored);
        state.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(21, 0, ["", "", "", "", "NewFriend", "Andromeda"], [])));
        Assert.Equal("Andromeda", state.Friends["NewFriend"].Presence);
        state.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(22, 0, ["", "", "", "", "NewFriend", ""], [])));
        Assert.DoesNotContain("NewFriend", state.Friends.Keys);

        Assert.Equal(28u, state.SelectPresenceRequestCode(true)); Assert.True(state.RestrictedStatus);
        state.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(26, 0, ["", "", "", "", "", ""], [])));
        Assert.True(state.RestrictedStatus);
        Assert.Equal("SYSTEM: All players may see you on their Friends List.", state.Messages[^1].Text);
        state.ResetForCharacterSelection();
        Assert.True(state.RestrictedStatus);
        Assert.Equal(29u, state.SelectPresenceRequestCode(false)); Assert.False(state.RestrictedStatus);
        state.ApplyClientChatEvent(Net7SocialWire.DecodeClientChatEvent(Net7SocialWireTests.ChatEvent(25, 0, ["", "", "", "", "", ""], [])));
        Assert.False(state.RestrictedStatus);
        Assert.Equal("SYSTEM: Only members of your Friends List may see if you are online.", state.Messages[^1].Text);

        string[,] exactClassTable =
        {
            { "Ter/Enf", "Ter/Tra", "Ter/Sct" },
            { "Jen/Def", "Jen/Skr", "Jen/Exp" },
            { "Pro/War", "Pro/Pvt", "Pro/Sen" },
        };
        for (uint race = 0; race < 3; race++)
            for (uint profession = 0; profession < 3; profession++)
                Assert.Equal(exactClassTable[(int)race, (int)profession], new Net7FindMemberRecord(99, 0, race, profession).CurrentClassLabel);

        state.ApplyFindMember([
            new Net7FindMemberRecord(1, 9, 0, 0),
            new Net7FindMemberRecord(2, 8, 2, 1),
            new Net7FindMemberRecord(3, 7, 9, 9),
        ]);
        Assert.Equal(2, state.GroupFinderCandidates.Count);
        Assert.Equal("Anonymous", state.GroupFinderCandidates[0].Name);
        Assert.Equal(7u, state.GroupFinderCandidates[0].NumericField);
        Assert.Equal("Ter/Enf", state.GroupFinderCandidates[0].CurrentClassLabel);
        Assert.False(state.GroupFinderCandidates[0].HasValidClassIndex);
        Assert.Equal("Peer", state.GroupFinderCandidates[1].Name);
        Assert.Equal("Pro/Pvt", state.GroupFinderCandidates[1].CurrentClassLabel);
    }

    [Fact]
    public void IgnoreNoticesNameBothPlayersAndSelfIgnoreRequestsErrorCue()
    {
        var state = new Net7SocialState();
        state.ApplyClientChatEvent(new(19, 0, "route", "Tavo", "", "", "Selina", "", []));
        Assert.Equal("COMPUTER: Tavo is now ignoring Selina.", state.Messages[^1].Text);
        Assert.Contains("Selina", state.Ignored);
        state.ApplyClientChatEvent(new(20, 0, "route", "Tavo", "", "", "Selina", "", []));
        Assert.Equal("COMPUTER: Tavo is no longer ignoring Selina.", state.Messages[^1].Text);
        Assert.DoesNotContain("Selina", state.Ignored);

        state.ApplyClientChatError(new(17, 10, "Tavo", "", ""));
        Assert.Equal("SYSTEM: You cannot ignore yourself.", state.Messages[^1].Text);
        Assert.Contains(state.PresentationEvents, e => e.Type == 19 &&
            e.Kind == Net7ChatPresentationKind.Error && e.Text == "Text_Error");
    }

    [Fact]
    public void GuildD0UsesExactTableAndWindowSpecificStructuredViews()
    {
        var state = new Net7SocialState();
        state.ApplyGuildMessage(new Net7GuildMessage(99, "Officer", "Builders"));
        Assert.Contains("Officer", state.Messages[^1].Text, StringComparison.Ordinal);
        Assert.Contains("Builders", state.Messages[^1].Text, StringComparison.Ordinal);

        state.GuildWindowActive = true;
        state.ApplyGuildMessage(new Net7GuildMessage(3, "Alpha (offline)\nBeta", ""));
        Assert.Equal(2, state.GuildRosterEntries.Count);
        Assert.Contains(state.GuildRosterEntries, x => x.Identity == "Alpha" && x.IsOffline);
        Assert.Contains(state.GuildRosterEntries, x => x.Identity == "Beta" && !x.IsOffline);

        state.GuildSecondarySubview = Net7GuildSecondarySubview.Second;
        state.ApplyGuildMessage(new Net7GuildMessage(2, "L1\nL2", ""));
        Assert.Equal(new[] { "L1", "L2" }, state.GuildSecondarySecondLines.ToArray());

        int before = state.Messages.Count;
        state.ApplyGuildMessage(new Net7GuildMessage(4, "x", "y"));
        Assert.Equal(before, state.Messages.Count);
        state.GuildWindowActive = false;
        state.ApplyGuildMessage(new Net7GuildMessage(4, "x", "y"));
        Assert.Equal(before + 1, state.Messages.Count);
    }

    [Theory]
    [InlineData(19u)]
    [InlineData(uint.MaxValue)]
    public void UnrecognizedChatErrorStillDisplaysResponseAndAcceptsNextPacket(uint status)
    {
        var state = new Net7GameplayState();
        Assert.True(state.Apply(Packet.ForOpcode(0x00A6,
            Net7SocialWireTests.ChatError(status, 1, "Peer", "", ""))));
        Assert.Equal("SYSTEM: Could not broadcast to Peer. (bad chat error)", state.Social.Messages[^1].Text);
        Assert.True(state.Apply(Packet.ForOpcode(0x00A6,
            Net7SocialWireTests.ChatError(4, 1, "Peer", "", ""))));
        Assert.Equal("SYSTEM: Peer is not online at this time.", state.Social.Messages[^1].Text);
        Assert.Equal(2, state.Social.Messages.Count);
    }

    [Fact]
    public void GameplayStateDispatchesAllSevenI06InboundFamilies()
    {
        var state = new Net7GameplayState(resolveObjectName: id => id == 2 ? "Peer" : null, localPlayerId: () => 1);
        Assert.True(state.Apply(Packet.ForOpcode(0x001E, new byte[] { 2, 0, 1, (byte)'X', 0 })));
        Assert.True(state.Apply(Packet.ForOpcode(0x0053, FindMemberOne(2))));
        Assert.True(state.Apply(Packet.ForOpcode(0x005F, ChatstreamPayload(2, 3, "hello"))));
        Assert.True(state.Apply(Packet.ForOpcode(0x00A4, Net7SocialWireTests.ChatList(1, "", ["Ignored"], []))));
        Assert.True(state.Apply(Packet.ForOpcode(0x00A5, Net7SocialWireTests.ChatEvent(25, 0, ["", "", "", "", "", ""], []))));
        Assert.True(state.Apply(Packet.ForOpcode(0x00A6, Net7SocialWireTests.ChatError(4, 1, "Peer", "", ""))));
        Assert.True(state.Apply(Packet.ForOpcode(0x00BD, Convert.FromHexString("020000000F00000000"))));
        Assert.False(state.Social.RestrictedStatus);
        Assert.Equal("SYSTEM: Only members of your Friends List may see if you are online.", state.Social.Messages[^2].Text);
        Assert.True(state.Social.GroupFormationVisualEnabled);
    }

    private static Net7MessageChatstreamUpdate Chatstream(uint shipId, byte type, string text)
        => Net7SocialWire.DecodeMessageChatstream(ChatstreamPayload(shipId, type, text));

    private static byte[] ChatstreamPayload(uint shipId, byte type, string text)
    {
        byte[] nlp = Net7SocialWireTests.PlayerNlpText(text); byte[] p = new byte[7 + nlp.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(0, 2), checked((ushort)nlp.Length)); p[2] = type; BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(3, 4), shipId); nlp.CopyTo(p, 7); return p;
    }

    private static byte[] FindMemberOne(uint id)
    {
        byte[] p = new byte[20]; BinaryPrimitives.WriteUInt32LittleEndian(p, 1); BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4, 4), id); BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8, 4), 7); BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(12, 4), 1); BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16, 4), 2); return p;
    }
}
