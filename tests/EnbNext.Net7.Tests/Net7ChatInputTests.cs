using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Social;

namespace EnbNext.Net7.Tests;

public sealed class Net7ChatInputTests
{
    [Fact]
    public void ChatListOwnersSuppressOnlyTheirResponseAndRetireOnCloseOrStreamChange()
    {
        var state = new Net7SocialState();
        var stream = state.StreamId;
        state.ApplyClientChatList(new(1, "", ["Old"], []));
        int history = state.Messages.Count;
        state.SetChatListWindow(stream, true, false, "");
        Assert.Empty(state.IgnoredRows);
        Assert.Contains("Old", state.Ignored); // Opening a view must not clear the message filter.
        state.ApplyClientChatEvent(new(19, 0, "", "", "", "", "New", "", []));
        Assert.Equal(new[] { "New" }, state.IgnoredRows);
        history = state.Messages.Count;
        state.ApplyClientChatList(new(1, "", ["Alice"], []));
        Assert.Equal(history, state.Messages.Count);
        Assert.Equal(new[] { "Alice" }, state.IgnoredRows);
        state.ApplyClientChatList(new(2, "42", ["Member"], []));
        Assert.Equal(history + 2, state.Messages.Count);
        state.SetChatListWindow(stream, false, true, "42");
        Assert.Empty(state.ChannelMembers["42"]);
        history = state.Messages.Count;
        state.ApplyClientChatList(new(2, "different context", ["Member"], []));
        Assert.Equal(history, state.Messages.Count);
        Assert.Equal(new[] { "Member" }, state.ChannelMembers["42"]);
        state.ApplyClientChatList(new(1, "", [], []));
        Assert.Equal(history + 2, state.Messages.Count);
        state.SetChatListWindow(stream, false, false, "");
        history = state.Messages.Count;
        state.ApplyClientChatList(new(2, "42", [], []));
        Assert.Equal(history + 2, state.Messages.Count);
        state.SetChatListWindow(stream, true, false, "");
        state.ResetForCharacterSelection();
        state.SetChatListWindow(stream, true, true, "42");
        Assert.False(state.IgnoreWindowActive);
        Assert.False(state.ChannelMembersWindowActive);
    }

    [Theory]
    [InlineData("yes", true, 28u)]
    [InlineData("no", false, 29u)]
    public void SavedPresencePreferenceSelectsRequestAndServerConfirmation(string value, bool restricted, uint code)
    {
        var options = EnbNext.Assets.EnbRetailPlayerOptionsCatalog.ParseText($"[BOOL-1]\nOptionName=RestrictedStatus\nOptionValue={value}");
        Assert.Equal(restricted, options.RestrictedStatus);
        var state = new Net7SocialState();
        Assert.Equal(code, state.SelectPresenceRequestCode(options.RestrictedStatus));
        Assert.Empty(state.Messages);
        state.ApplyClientChatEvent(new(restricted ? 25u : 26u, 0, "", "", "", "", "", "", []));
        Assert.Contains(state.Messages, m => m.Type == 19 && m.Text.Contains(restricted ? "Only members of your Friends List" : "All players may see"));
    }

    private static readonly Net7ChatPolicyContext Local = new(17, Net7SelectedChatChannel.Local, false, false);

    [Fact]
    public void SelectedReplyRetainsItsPeerWhileSlashReplyUsesTheLatestArrival()
    {
        var selected = Net7ChatInput.Plan(new("hello", "Reply", "Earlier"), Local, 41, "Later", false);
        Assert.Equal("Earlier", selected.Private!.TargetIdentity);
        Assert.Equal(Net7PrivateChatProducer.SelectedReply, selected.Private.Producer);
        Assert.Equal(Net7GameplayWire.BuildClientChatRequest(41, 1, "Earlier", "", "hello",
            Net7GameplayWire.BuildPlayerNlpStream([new(1, 0, "hello")])), Assert.Single(selected.Packets).Payload);
        var slash = Net7ChatInput.Plan(new("/r hello", "Reply", "Earlier"), Local, 41, "Later", false);
        Assert.Equal("Later", slash.Private!.TargetIdentity);
        var missing = Net7ChatInput.Plan(new("hello", "Reply"), Local, 41, "Later", false);
        Assert.Empty(missing.Packets);
        Assert.Equal("No one to reply to.", missing.LocalError);
    }

    [Fact]
    public void AvatarSpeechSendsRichStreamThenLocalText()
    {
        var submission = Net7ChatInput.Plan(new("Hello."), Local, 41, null, true);
        Assert.Equal(new ushort[] { 0x005E, 0x0033 }, submission.Packets.Select(p => p.Opcode));
        Assert.Equal(Net7GameplayWire.BuildChatstream(17, Net7GameplayWire.BuildPlayerNlpStream([new(1, 0, "Hello.")])), submission.Packets[0].Payload);
        Assert.Equal(Net7GameplayWire.BuildPlayerCommand(17, Net7ChatCommandType.LocalArea, "Hello."), submission.Packets[1].Payload);
    }

    [Fact]
    public void PrivateSelectionDoesNotSwallowExplicitChannelCommands()
    {
        var submission = Net7ChatInput.Plan(new("/g hello", "Secure", "Peer"), Local, 41, null, false);
        Assert.Null(submission.Private);
        Assert.Equal(Net7GameplayWire.BuildPlayerCommand(17, Net7ChatCommandType.Group, "hello"), Assert.Single(submission.Packets).Payload);
    }

    [Theory]
    [InlineData("/tell Peer hello there", "Peer", "hello there")]
    [InlineData("/t Peer hello there", "Peer", "hello there")]
    [InlineData("/r hello there", "ReplyPeer", "hello there")]
    public void PrivateEchoContainsBodyAndUsesCorrectPeer(string input, string peer, string body)
    {
        var submission = Net7ChatInput.Plan(new(input), Local, 41, "ReplyPeer", false);
        Assert.Equal(peer, submission.Private!.TargetIdentity);
        Assert.Equal(body, submission.EchoText);
        Assert.Single(submission.Packets);
    }

    [Theory]
    [InlineData("/chat channel 42 create", 4, "", "42", "")]
    [InlineData("/chat channel 42 say Hello everyone", 0, "", "42", "Hello everyone")]
    [InlineData("/chat channel 42 enter secret", 6, "", "42", "secret")]
    [InlineData("/chat channel 42 exit", 7, "", "42", "")]
    [InlineData("/chat friend Peer", 8, "Peer", "", "")]
    [InlineData("/chat channel 42 invite Peer", 12, "Peer", "42", "")]
    [InlineData("/chat channel 42 members", 27, "", "42", "")]
    [InlineData("/chat list friends", 24, "", "", "")]
    [InlineData("/chat list all_channels", 26, "", "", "")]
    [InlineData("/chat all Hello everyone", 3, "", "", "Hello everyone")]
    public void ChatControlGrammarUsesAuditedOperands(string text, uint code, string player, string channel, string value)
    {
        var submission = Net7ChatInput.Plan(new(text), Local, 41, null, false);
        Assert.Null(submission.LocalError);
        Assert.Equal(Net7GameplayWire.BuildClientChatRequest(41, code, player, channel, value, []), Assert.Single(submission.Packets).Payload);
    }

    [Theory]
    [InlineData("/chat invite Peer")]
    [InlineData("/chat 30")]
    [InlineData("/reply hello")]
    public void InvalidInputDoesNotSend(string text)
    {
        var submission = Net7ChatInput.Plan(new(text), Local, 41, null, false);
        Assert.Empty(submission.Packets);
        Assert.NotNull(submission.LocalError);
    }

    [Theory]
    [InlineData("Sector 1", 2)]
    [InlineData("Group 5", 4)]
    [InlineData("Guild 6", 8)]
    [InlineData("General", 9)]
    [InlineData("Out of Context", 10)]
    [InlineData("New Players", 11)]
    [InlineData("Market", 12)]
    [InlineData("42", 13)]
    [InlineData("Terran", 14)]
    [InlineData("Warriors", 15)]
    public void NamedMessagesUseRetailHistoryType(string channel, byte type)
    {
        var state = new Net7SocialState();
        state.ApplyClientChatEvent(new(3, 0, "Peer", "Display", "", channel + "`suffix", "message", "", []));
        Assert.Equal(type, Assert.Single(state.Messages).Type);
    }

    [Fact]
    public void ChannelAndFriendFailuresReachHistoryWithoutChangingReply()
    {
        var state = new Net7SocialState();
        state.ApplyClientChatError(new(14, 0, "Peer", "42", ""));
        state.ApplyClientChatError(new(7, 8, "Peer", "", ""));
        Assert.Contains("Could not speak on 42", state.Messages[0].Text);
        Assert.Contains("already on your friend list", state.Messages[1].Text);
        Assert.Null(state.ReplyIdentity);
    }

    [Fact]
    public void SequenceSurvivesHistoryEvictionAndResetsWithCharacter()
    {
        var state = new Net7SocialState();
        for (int i = 0; i < 205; i++) state.ApplyBaseMessage(new(7, "line"));
        Assert.Equal(6, state.Messages[0].Sequence);
        Assert.Equal(205, state.Messages[^1].Sequence);
        var stream = state.StreamId;
        state.ResetForHandoff(); Assert.Equal(stream, state.StreamId);
        state.ResetForCharacterSelection(); Assert.NotEqual(stream, state.StreamId);
    }

    [Fact]
    public void ManualAvatarGestureUsesOnlySubmitStream()
    {
        var request = new Net7ChatInputRequest("/handwave") { AvatarRecords = new Net7PlayerNlpRecord[] { new(2, 33) } };
        var submission = Net7ChatInput.Plan(request, Local, 41, null, true);
        Assert.Equal((ushort)0x005E, Assert.Single(submission.Packets).Opcode);
        Assert.Equal(Net7GameplayWire.BuildChatstream(17, Net7GameplayWire.BuildPlayerNlpStream(request.AvatarRecords)), submission.Packets[0].Payload);
    }

    [Fact]
    public void ChatSnapshotIsReusedUntilMessagesChange()
    {
        var state = new Net7SocialState();
        var first = EnbNext.Presentation.EnbSceneChatSnapshot.From(state);
        Assert.Same(first, EnbNext.Presentation.EnbSceneChatSnapshot.From(state));
        state.ApplyBaseMessage(new(22, "Attention"));
        var second = EnbNext.Presentation.EnbSceneChatSnapshot.From(state);
        Assert.NotSame(first, second);
        Assert.Empty(second.Lines);
        Assert.Single(second.Events);
    }

    [Fact]
    public void PresenceAndGmNoticesUseAuditedDisplayOperands()
    {
        var state = new Net7SocialState();
        foreach (uint kind in new uint[] { 1, 2, 27, 28 })
            state.ApplyClientChatEvent(new(kind, 0, "Actor", "Display", "Other", "Channel", "Target", "Unused", []));
        Assert.Equal(new[] { "COMPUTER: Display logged in.", "COMPUTER: Display logged out.",
            "COMPUTER: Target has been gagged by GM Actor.", "COMPUTER: GM Actor has removed the gag on Target." }, state.Messages.Select(m => m.Text));
    }

    [Fact]
    public void MembershipEventsUpdateListWithoutAddingChatLines()
    {
        var state = new Net7SocialState();
        state.ApplyClientChatList(new(2, "42", new[] { "Existing" }, Array.Empty<string>()));
        int historyCount = state.Messages.Count;
        state.ApplyClientChatEvent(new(7, 0, "Joined", "Display", "", "42", "", "", []));
        Assert.Equal(new[] { "Existing", "Joined" }, state.ChannelMembers["42"]);
        state.ApplyClientChatEvent(new(8, 0, "Existing", "Display", "", "42", "", "", []));
        Assert.Equal("Joined", Assert.Single(state.ChannelMembers["42"]));
        Assert.Equal(historyCount, state.Messages.Count);
    }

    [Fact]
    public void IgnorePresentationPreservesSortedRowsAndRefreshesIndependentlyOfFilteringSet()
    {
        var state = new Net7SocialState();
        var response = new Net7ClientChatListUpdate(1, "", ["alice", "Zed", "Alice", "Alice"], []);
        state.ApplyClientChatList(response);
        var first = EnbNext.Presentation.EnbSceneChatSnapshot.From(state);
        Assert.Equal(new[] { "Alice", "Alice", "Zed", "alice" }, first.Ignored);
        state.ApplyClientChatList(response);
        Assert.Equal(first.IgnoredRevision + 1, EnbNext.Presentation.EnbSceneChatSnapshot.From(state).IgnoredRevision);
        state.ApplyClientChatEvent(new(20, 0, "", "", "", "", "Alice", "", []));
        Assert.Equal(new[] { "Alice", "Zed", "alice" }, EnbNext.Presentation.EnbSceneChatSnapshot.From(state).Ignored);
        state.ApplyClientChatEvent(new(19, 0, "", "", "", "", "Bob", "", []));
        Assert.Equal(new[] { "Alice", "Bob", "Zed", "alice" }, EnbNext.Presentation.EnbSceneChatSnapshot.From(state).Ignored);
        state.ResetForCharacterSelection();
        Assert.Empty(EnbNext.Presentation.EnbSceneChatSnapshot.From(state).Ignored);
    }

    [Fact]
    public void MemberRefreshRetainsOrdinalNamesAndSignalsIdenticalResponses()
    {
        var state = new Net7SocialState();
        var response = new Net7ClientChatListUpdate(2, "42", ["alice", "Zed", "Alice"], []);
        state.ApplyClientChatList(response);
        var first = EnbNext.Presentation.EnbSceneChatSnapshot.From(state);
        Assert.Equal(new[] { "Alice", "Zed", "alice" }, first.ChannelMembers["42"]);
        state.ApplyClientChatList(response);
        var repeated = EnbNext.Presentation.EnbSceneChatSnapshot.From(state);
        Assert.Equal(first.ChannelMemberRevisions["42"] + 1, repeated.ChannelMemberRevisions["42"]);
        state.ApplyClientChatEvent(new(7, 0, "Alice", "", "", "42", "", "", []));
        Assert.Equal(new[] { "Alice", "Alice", "Zed", "alice" }, state.ChannelMembers["42"]);
        state.ApplyClientChatEvent(new(8, 0, "Alice", "", "", "42", "", "", []));
        Assert.Equal(new[] { "Alice", "Zed", "alice" }, state.ChannelMembers["42"]);
        long revision = state.ChannelMemberRevisions["42"];
        state.ApplyClientChatEvent(new(8, 0, "Absent", "", "", "42", "", "", []));
        Assert.Equal(revision + 1, state.ChannelMemberRevisions["42"]);
        state.ResetForCharacterSelection();
        Assert.Empty(EnbNext.Presentation.EnbSceneChatSnapshot.From(state).ChannelMemberRevisions);
    }

    [Fact]
    public void LocalPrivateMembershipFeedsChatDestinationsAndLeaveRemovesIt()
    {
        var state = new Net7SocialState();
        state.ApplyClientChatEvent(new(7, 1, "Tavo", "Tavo", "", "101", "", "", []));
        Assert.Contains("101", state.CurrentChannels);
        Assert.Contains("101", EnbNext.Presentation.EnbSceneChatSnapshot.From(state).Channels);
        Assert.Equal("101", state.PrivateChannel);
        state.ApplyClientChatEvent(new(23, 1, "", "", "Tavo", "101", "", "", []));
        Assert.True(state.PrivateChannelOwner);
        state.ApplyClientChatEvent(new(24, 0, "", "", "Other", "101", "", "", []));
        Assert.True(state.PrivateChannelOwner);
        state.ApplyClientChatEvent(new(24, 1, "", "", "Tavo", "101", "", "", []));
        Assert.False(state.PrivateChannelOwner);
        state.ApplyClientChatEvent(new(8, 1, "Tavo", "Tavo", "", "101", "", "", []));
        Assert.DoesNotContain("101", state.CurrentChannels);
        Assert.Equal("", state.PrivateChannel);
        state.ApplyClientChatEvent(new(9, 0, "Tavo", "Tavo", "", "102", "", "", []));
        Assert.Contains("102", state.CurrentChannels);
        Assert.True(state.PrivateChannelOwner);
    }

    [Theory]
    [InlineData("/gc message Welcome to the guild", Net7ExactServerChatCommand.GuildMessage, "Welcome to the guild", null)]
    [InlineData("/gc tag 2 \"Senior Officer\"", Net7ExactServerChatCommand.GuildTag, "2", "Senior Officer")]
    public void GuildTextPreservesSpaces(string input, Net7ExactServerChatCommand command, string arg1, string? arg2)
    {
        var submission = Net7ChatInput.Plan(new(input), Local, 41, null, false);
        Assert.Equal(Net7SocialChatPolicy.PlanExactServerCommand(Local, command, arg1, arg2).Payload, Assert.Single(submission.Packets).Payload);
    }
}
