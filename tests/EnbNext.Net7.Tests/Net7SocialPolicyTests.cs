using System.Buffers.Binary;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Social;

namespace EnbNext.Net7.Tests;

public sealed class Net7SocialPolicyTests
{
    [Fact]
    public void SelectedChannelsEnforceMembershipWhileDirectGroupGuildSlashDoesNot()
    {
        var groupSelected = new Net7ChatPolicyContext(0x11223344, Net7SelectedChatChannel.Group, false, false);
        Net7ChatCommandPlan blockedGroup = Net7SocialChatPolicy.PlanSelectedChannel(groupSelected, "hello");
        Assert.False(blockedGroup.ShouldSend);
        Assert.Equal("Not in a group.", blockedGroup.LocalError);
        Assert.Empty(blockedGroup.Payload);

        Net7ChatCommandPlan directGroup = Net7SocialChatPolicy.PlanDirectGroupOrGuildSlash(groupSelected, "/g hello");
        Assert.True(directGroup.ShouldSend);
        Assert.Equal(Net7ChatCommandOrigin.DirectGroupSlash, directGroup.Origin);
        Assert.Equal((byte)Net7ChatCommandType.Group, directGroup.Payload[4]);
        Assert.False(Net7SocialChatPolicy.PlanDirectGroupOrGuildSlash(groupSelected, "/G hello").ShouldSend);

        Net7ChatCommandPlan emptyDirectGroup = Net7SocialChatPolicy.PlanDirectGroupOrGuildSlash(groupSelected, "/g ");
        Assert.True(emptyDirectGroup.ShouldSend);
        Assert.Equal(string.Empty, DecodePlayerCommandText(emptyDirectGroup.Payload));

        var guildSelected = groupSelected with { SelectedChannel = Net7SelectedChatChannel.Guild };
        Net7ChatCommandPlan blockedGuild = Net7SocialChatPolicy.PlanSelectedChannel(guildSelected, "hello");
        Assert.False(blockedGuild.ShouldSend);
        Assert.Equal("Not in a guild.", blockedGuild.LocalError);
        Assert.Empty(blockedGuild.Payload);

        Net7ChatCommandPlan directGuild = Net7SocialChatPolicy.PlanDirectGroupOrGuildSlash(guildSelected, "/GU hello");
        Assert.True(directGuild.ShouldSend);
        Assert.Equal((byte)Net7ChatCommandType.Guild, directGuild.Payload[4]);

        Net7ChatCommandPlan emptyDirectGuild = Net7SocialChatPolicy.PlanDirectGroupOrGuildSlash(guildSelected, "/gu ");
        Assert.True(emptyDirectGuild.ShouldSend);
        Assert.Equal(string.Empty, DecodePlayerCommandText(emptyDirectGuild.Payload));

        Net7ChatCommandPlan unknownGroup = Net7SocialChatPolicy.PlanUnknownSlashFallback(groupSelected, "/not-a-command");
        Assert.False(unknownGroup.ShouldSend);
        Assert.Equal("Not in a group.", unknownGroup.LocalError);
        Assert.Equal(Net7ChatCommandOrigin.UnknownSlashFallback, unknownGroup.Origin);
    }

    [Fact]
    public void UnknownSlashFallsBackToSelectedChannelAndExactServerCommandsStayExact()
    {
        var context = new Net7ChatPolicyContext(7, Net7SelectedChatChannel.Local, true, true);
        Net7ChatCommandPlan unknown = Net7SocialChatPolicy.PlanUnknownSlashFallback(context, "/not-a-command arg");
        Assert.Equal(Net7ChatCommandOrigin.UnknownSlashFallback, unknown.Origin);
        Assert.Equal((byte)Net7ChatCommandType.LocalArea, unknown.Payload[4]);
        Assert.Contains("/not-a-command arg", DecodePlayerCommandText(unknown.Payload), StringComparison.Ordinal);

        Assert.Equal("/resetship", DecodePlayerCommandText(Net7SocialChatPolicy.PlanExactServerCommand(context, Net7ExactServerChatCommand.ResetShip).Payload));
        Assert.Equal("/founder_enable", DecodePlayerCommandText(Net7SocialChatPolicy.PlanExactServerCommand(context, Net7ExactServerChatCommand.FounderEnable).Payload));
        Assert.Equal("/gc recruit Pilot", DecodePlayerCommandText(Net7SocialChatPolicy.PlanExactServerCommand(context, Net7ExactServerChatCommand.GuildRecruit, "Pilot").Payload));
        Assert.Equal("/gc tag Pilot \"TAG\"", DecodePlayerCommandText(Net7SocialChatPolicy.PlanExactServerCommand(context, Net7ExactServerChatCommand.GuildTag, "Pilot", "TAG").Payload));
    }

    [Fact]
    public void SpecialGroupFormatterUsesOwnerTailAfterFirstSpace()
    {
        var context = new Net7ChatPolicyContext(1, Net7SelectedChatChannel.Group, true, true);
        Assert.Equal("<S>1Tail`Dialog", DecodePlayerCommandText(Net7SocialChatPolicy.PlanGroupDialogSignal(context, true, "Owner Tail", "Dialog").Payload));
        Assert.Equal("<S>0Owner`Dialog", DecodePlayerCommandText(Net7SocialChatPolicy.PlanGroupDialogSignal(context, false, "Owner", "Dialog").Payload));
    }

    [Fact]
    public void NamedChannelRoutingPreservesRetailCaseSensitivityAndLocalNoSendMessages()
    {
        Net7ClientChatRequestPlan radioMissing = Net7SocialChatPolicy.PlanSelectedRadioChannelSend(1, null, "hello");
        Assert.False(radioMissing.ShouldSend);
        Assert.Equal("Not subscribed to any channels.", radioMissing.LocalError);

        Net7ClientChatRequestPlan publicMissing = Net7SocialChatPolicy.PlanSelectedMonitoredChannelSend(1, null, "hello");
        Assert.False(publicMissing.ShouldSend);
        Assert.Equal("Not monitoring any public channels.", publicMissing.LocalError);

        Net7ClientChatRequestPlan lower = Net7SocialChatPolicy.PlanNamedChannelAlias(1, "/gen", "hello");
        Assert.True(lower.ShouldSend);
        Assert.Equal((string.Empty, "General", "hello"), ReadA3Args(lower.Payload));

        Net7ClientChatRequestPlan upper = Net7SocialChatPolicy.PlanNamedChannelAlias(1, "/GEN", "hello");
        Assert.False(upper.ShouldSend);
        Assert.Null(upper.LocalError);

        Net7ClientChatRequestPlan disabled = Net7SocialChatPolicy.PlanNamedChannelAlias(1, "/gen", "hello", channelEnabled: false);
        Assert.False(disabled.ShouldSend);
        Assert.Equal("Not monitoring that channel.", disabled.LocalError);

        Net7ClientChatRequestPlan empty = Net7SocialChatPolicy.PlanNamedChannelAlias(1, "/gen", string.Empty);
        Assert.False(empty.ShouldSend);
        Assert.Equal("No message to send.", empty.LocalError);

        Net7ClientChatRequestPlan radioAliasMissing = Net7SocialChatPolicy.PlanNamedChannelAlias(1, "/rad", "hello", null);
        Assert.False(radioAliasMissing.ShouldSend);
        Assert.Equal("Not monitoring a private channel.", radioAliasMissing.LocalError);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 42 ")]
    public void PrivateJoinPlannerPreservesTheExplicitLiteralBuffer(string channel)
    {
        var plan=Net7SocialChatPolicy.PlanChatControlRequest(1,Net7ClientChatRequestKind.EnterChannel,channel:channel);
        Assert.True(plan.ShouldSend);
        Assert.Equal(6u,BinaryPrimitives.ReadUInt32LittleEndian(plan.Payload.AsSpan(4,4)));
        Assert.Equal((string.Empty,channel,string.Empty),ReadA3Args(plan.Payload));
        Assert.False(Net7SocialChatPolicy.PlanChatControlRequest(1,Net7ClientChatRequestKind.EnterChannel).ShouldSend);
    }

    [Fact]
    public void ChatControlPlannerPinsCurrentRequestCodeArgumentShapes()
    {
        Net7ClientChatRequestPlan invite = Net7SocialChatPolicy.PlanChatControlRequest(
            1, Net7ClientChatRequestKind.Invite, player: "Peer", channel: "Raid");
        Assert.True(invite.ShouldSend);
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(invite.Payload.AsSpan(4, 4)));
        Assert.Equal(("Peer", "Raid", string.Empty), ReadA3Args(invite.Payload));

        Net7ClientChatRequestPlan enter = Net7SocialChatPolicy.PlanChatControlRequest(
            1, Net7ClientChatRequestKind.EnterChannel, channel: "Raid", value: "token");
        Assert.True(enter.ShouldSend);
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(enter.Payload.AsSpan(4, 4)));
        Assert.Equal((string.Empty, "Raid", "token"), ReadA3Args(enter.Payload));

        Assert.False(Net7SocialChatPolicy.PlanChatControlRequest(
            1, Net7ClientChatRequestKind.Invite, player: "Peer").ShouldSend);
        Assert.False(Net7SocialChatPolicy.PlanChatControlRequest(
            1, (Net7ClientChatRequestKind)2).ShouldSend);
    }

    [Fact]
    public void PrivateProducerPlansPreserveBlobEchoSecureAndReplyDifferences()
    {
        byte[] nlp = Net7SocialWireTests.PlayerNlpText("hello");
        Net7PrivateChatPlan tell = Net7SocialChatPolicy.PlanPrivateSend(0x1234, "Peer", "hello", Net7PrivateChatProducer.Tell, nlp);
        Assert.True(tell.ShouldSend); Assert.True(tell.CreateLocalEcho); Assert.True(tell.PromoteSecureTarget); Assert.Contains(nlp[0], tell.Payload);

        Net7PrivateChatPlan tellWithoutSource = Net7SocialChatPolicy.PlanPrivateSend(0x1234, "Peer", "hello", Net7PrivateChatProducer.Tell, ReadOnlySpan<byte>.Empty);
        Assert.False(tellWithoutSource.ShouldSend);
        Assert.Equal("Peer", tellWithoutSource.TargetIdentity);
        Assert.True(tellWithoutSource.PromoteSecureTarget);
        Assert.Null(tellWithoutSource.LocalError);

        Net7PrivateChatPlan selectedSecureWithoutSource = Net7SocialChatPolicy.PlanPrivateSend(0x1234, "Peer", "hello", Net7PrivateChatProducer.SelectedSecure, ReadOnlySpan<byte>.Empty);
        Assert.True(selectedSecureWithoutSource.ShouldSend);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(selectedSecureWithoutSource.Payload.AsSpan(selectedSecureWithoutSource.Payload.Length - 4, 4)));

        Net7PrivateChatPlan chatTell = Net7SocialChatPolicy.PlanPrivateSend(0x1234, "Peer", "hello", Net7PrivateChatProducer.ChatTell, nlp);
        Assert.True(chatTell.ShouldSend); Assert.False(chatTell.CreateLocalEcho); Assert.False(chatTell.PromoteSecureTarget);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(chatTell.Payload.AsSpan(chatTell.Payload.Length - 4, 4)));

        Net7PrivateChatPlan selectedMissing = Net7SocialChatPolicy.PlanReply(1, null, "hi", Net7PrivateChatProducer.SelectedReply, nlp);
        Assert.False(selectedMissing.ShouldSend);
        Assert.Equal("No one to reply to.", selectedMissing.LocalError);
        Net7PrivateChatPlan slashMissing = Net7SocialChatPolicy.PlanReply(1, null, "hi", Net7PrivateChatProducer.Reply, nlp);
        Assert.False(slashMissing.ShouldSend);
        Assert.Equal("Unable to reply - nothing to reply to.", slashMissing.LocalError);

        Net7PrivateChatPlan bareReply = Net7SocialChatPolicy.PlanReply(1, "Current", string.Empty, Net7PrivateChatProducer.Reply, nlp);
        Assert.False(bareReply.ShouldSend);
        Assert.True(bareReply.OpenPrivateUi);
        Assert.Null(bareReply.LocalError);

        Net7PrivateChatPlan replyWithoutSource = Net7SocialChatPolicy.PlanReply(1, "Current", "hi", Net7PrivateChatProducer.Reply, ReadOnlySpan<byte>.Empty);
        Assert.False(replyWithoutSource.ShouldSend);
        Assert.False(replyWithoutSource.OpenPrivateUi);

        Net7PrivateChatPlan reply = Net7SocialChatPolicy.PlanReply(1, "Current", "hi", Net7PrivateChatProducer.Reply, nlp);
        Assert.Equal("Current", reply.TargetIdentity); Assert.False(reply.PromoteSecureTarget);

        Assert.Equal("Usage: /tell <player> <message>", Net7SocialChatPolicy.PlanPrivateSend(1, "Peer", string.Empty, Net7PrivateChatProducer.Tell, nlp).LocalError);
        Assert.Equal("Usage: /t <player> <message>", Net7SocialChatPolicy.PlanPrivateSend(1, "Peer", string.Empty, Net7PrivateChatProducer.TellAlias, nlp).LocalError);
    }

    [Fact]
    public void PresenceAndPresentationActivationUseExactA3CodesWithEmptyArguments()
    {
        byte[] restricted = Net7SocialChatPolicy.BuildPresenceRequest(0x11223344, true);
        byte[] unrestricted = Net7SocialChatPolicy.BuildPresenceRequest(0x11223344, false);
        byte[] activation = Net7SocialChatPolicy.BuildPresentationActivationRequest(0x11223344);
        Assert.Equal(28u, BinaryPrimitives.ReadUInt32LittleEndian(restricted.AsSpan(4, 4)));
        Assert.Equal(29u, BinaryPrimitives.ReadUInt32LittleEndian(unrestricted.AsSpan(4, 4)));
        Assert.Equal(30u, BinaryPrimitives.ReadUInt32LittleEndian(activation.AsSpan(4, 4)));
        Assert.Equal(restricted.Length, unrestricted.Length);
        Assert.Equal(restricted.Length, activation.Length);
    }

    [Fact]
    public void GuildCatalogContainsAll132CurrentRows()
    {
        for (uint type = 0; type <= 131; type++)
            Assert.Equal(type, Net7GuildMessageCatalog.Resolve(type, "Other", "Guild")!.MessageType);
        Assert.Null(Net7GuildMessageCatalog.Resolve(132, "", ""));
    }

    private static (string Arg1, string Arg2, string Arg3) ReadA3Args(byte[] payload)
    {
        int offset = 8;
        return (ReadShared(payload, ref offset), ReadShared(payload, ref offset), ReadShared(payload, ref offset));
    }

    private static string ReadShared(byte[] payload, ref int offset)
    {
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset, 2));
        offset += 2;
        string value = System.Text.Encoding.Latin1.GetString(payload.AsSpan(offset, length));
        offset += length;
        return value;
    }

    private static string DecodePlayerCommandText(byte[] payload)
    {
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(5, 2));
        return System.Text.Encoding.Latin1.GetString(payload.AsSpan(7, len - 1));
    }
}
