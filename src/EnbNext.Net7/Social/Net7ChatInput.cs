using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Social;

public sealed record Net7ChatInputRequest(string Text, string Destination = "Local", string? Target = null)
{
    public IReadOnlyList<Net7PlayerNlpRecord>? AvatarRecords { get; init; }
    // Invoked only after routing selects an NLP producer; null means no local
    // avatar, distinct from an initialized stream containing no records.
    public Func<string,IReadOnlyList<Net7PlayerNlpRecord>?>? AvatarProducer {get;init;}
}
public sealed record Net7ChatSubmission(IReadOnlyList<(ushort Opcode, byte[] Payload)> Packets,
    string? LocalError = null, Net7PrivateChatPlan? Private = null, string? EchoText = null);

/// <summary>UI-independent dispatch onto the audited producers.</summary>
public static class Net7ChatInput
{
    /// <summary>The chat message type of a local refusal: 00749590 tell usage is type 6; 00749A70 reply with nothing to
    /// reply to, 0065CCD0 Guild/Group membership and empty channel denials are type 17; anything else Next reports as
    /// type 19 (CHAT-D01/D35).</summary>
    public static byte LocalErrorType(string error) => error switch
    {
        _ when error.StartsWith("Usage: /", StringComparison.Ordinal) => 6,
        "Unable to reply - nothing to reply to." or "Not in a guild." or "Not in a group." or "Not subscribed to any channels."
            or "Not monitoring any public channels." => 17,
        _ => 19,
    };

    public static Net7ChatSubmission Plan(Net7ChatInputRequest input, Net7ChatPolicyContext context,
        uint startSession, string? reply, bool avatarMode, uint? avatarObjectId = null)
    {
        string text = input.Text;
        if (string.IsNullOrEmpty(text)) return new([]);
        if (text.Contains('\0')) return new([], "Messages cannot contain a NUL character.");
        IReadOnlyList<Net7PlayerNlpRecord>? Produce(string body)
            =>input.AvatarProducer is {} producer?producer(body):input.AvatarRecords??[new(1,0,body)];
        string[] words = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        string command = words.ElementAtOrDefault(0)??"";
        if (!avatarMode && Net7ShipEmote.FromCommand(command) is uint shipEmote)
            return new([(0x00A1, Net7GameplayWire.BuildTriggerEmote(avatarObjectId ?? context.LocalPlayerId, shipEmote))]);
        string tail = text.Length > command.Length ? text[(command.Length + 1)..] : "";
        Net7ChatSubmission Private(bool slash)
        {
            bool isReply = slash?command is "/reply" or "/r":input.Destination == "Reply";
            string target = slash && !isReply ? words.ElementAtOrDefault(1) ?? "" : input.Target ?? "";
            // 00749590 trims the tell recipient and its final remainder (CHAT-D34).
            string body = slash ? isReply ? tail : (words.ElementAtOrDefault(2) ?? "").Trim() : text;
            var records=body.Length>0 && (!isReply || !string.IsNullOrEmpty(slash?reply:input.Target))?Produce(body):null;
            byte[] nlp=records is null?[]:Net7GameplayWire.BuildPlayerNlpStream(records);
            Net7PrivateChatPlan plan = isReply
                ? Net7SocialChatPolicy.PlanReply(startSession, slash ? reply : input.Target, body, slash?command == "/r" ? Net7PrivateChatProducer.ReplyAlias : Net7PrivateChatProducer.Reply : Net7PrivateChatProducer.SelectedReply, nlp)
                : Net7SocialChatPolicy.PlanPrivateSend(startSession, target, body, slash?command == "/t" ? Net7PrivateChatProducer.TellAlias : Net7PrivateChatProducer.Tell : Net7PrivateChatProducer.SelectedSecure, nlp);
            return new(plan.ShouldSend ? [(plan.Opcode, plan.Payload)] : [], plan.LocalError, plan, body);
        }
        if(command is "/tell" or "/t" or "/reply" or "/r")return Private(true);
        if (command == "/chat") return Control(startSession, tail);
        if (command is "/gen" or "/ooc" or "/mkt" or "/new" or "/jen" or "/ter" or "/pro" or "/exp" or "/tra" or "/war" or "/sen" or "/def" or "/enf" or "/rad")
        {
            var plan = Net7SocialChatPolicy.PlanNamedChannelAlias(startSession, command, tail, input.Target);
            return new(plan.ShouldSend ? [(plan.Opcode, plan.Payload)] : [], plan.LocalError);
        }
        var direct = Net7SocialChatPolicy.PlanDirectGroupOrGuildSlash(context, text);
        if (direct.ShouldSend) return new([(direct.Opcode, direct.Payload)]);
        if (command == "/resetship")
        {
            var reset = Net7SocialChatPolicy.PlanExactServerCommand(context, Net7ExactServerChatCommand.ResetShip);
            return new([(reset.Opcode, reset.Payload)]);
        }
        if (command == "/gc")
        {
            string verb = words.ElementAtOrDefault(1) ?? "";
            string? argument = words.ElementAtOrDefault(2);
            Net7ExactServerChatCommand? guild = verb switch
            {
                "list" when argument == "all" => Net7ExactServerChatCommand.GuildListAll,
                "recruit" => Net7ExactServerChatCommand.GuildRecruit,
                "message" => Net7ExactServerChatCommand.GuildMessage,
                "stats" => Net7ExactServerChatCommand.GuildStats,
                "create" => Net7ExactServerChatCommand.GuildCreate,
                "disband" => Net7ExactServerChatCommand.GuildDisband,
                "tag" => Net7ExactServerChatCommand.GuildTag,
                "publicstats" when argument == "on" => Net7ExactServerChatCommand.GuildPublicStatsOn,
                "publicstats" when argument == "off" => Net7ExactServerChatCommand.GuildPublicStatsOff,
                "leave" => Net7ExactServerChatCommand.GuildLeave,
                "remove" => Net7ExactServerChatCommand.GuildRemove,
                "promote" => Net7ExactServerChatCommand.GuildPromote,
                "demote" => Net7ExactServerChatCommand.GuildDemote,
                "contributions" => Net7ExactServerChatCommand.GuildContributions,
                _ => null,
            };
            if (guild is { } action)
            {
                try
                {
                    string? second = null;
                    if (action is Net7ExactServerChatCommand.GuildMessage or Net7ExactServerChatCommand.GuildCreate)
                        argument = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(2);
                    if (action == Net7ExactServerChatCommand.GuildTag)
                    {
                        var tag = text.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
                        argument = tag.ElementAtOrDefault(2);
                        second = tag.ElementAtOrDefault(3)?.Trim('"');
                    }
                    var packet = Net7SocialChatPolicy.PlanExactServerCommand(context, action, argument, second);
                    return new([(packet.Opcode, packet.Payload)]);
                }
                catch (ArgumentException) { return new([], "Missing guild command argument."); }
            }
        }
        // 65CCD0 applies the selected destination after slash dispatch returns
        // unhandled, including an unknown slash in Secure/Reply.
        if(input.Destination is "Secure" or "Reply")return Private(false);
        if (input.Destination == "Channel")
        {
            var channel = Net7SocialChatPolicy.PlanSelectedMonitoredChannelSend(startSession, input.Target, text);
            return new(channel.ShouldSend ? [(channel.Opcode, channel.Payload)] : [], channel.LocalError);
        }
        if(avatarMode && context.SelectedChannel is Net7SelectedChatChannel.Local or Net7SelectedChatChannel.Broadcast)
        {
            var records=Produce(text);
            if(records is null)return new([]);
            var rich=new List<(ushort,byte[])>{(0x005E,Net7GameplayWire.BuildChatstream(avatarObjectId??context.LocalPlayerId,Net7GameplayWire.BuildPlayerNlpStream(records)))};
            if(records.Any(r=>r.RecordType==1))
            {
                string visible=string.Concat(records.Where(r=>r.RecordType==1).Select(r=>r.TextPayload));
                var normalized=Net7SocialChatPolicy.PlanSelectedChannel(context,visible);
                if(normalized.ShouldSend)rich.Add((normalized.Opcode,normalized.Payload));
            }
            return new(rich);
        }
        var ordinary = Net7SocialChatPolicy.PlanSelectedChannel(context, text);
        if (!ordinary.ShouldSend) return new([], ordinary.LocalError);
        var packets = new List<(ushort, byte[])>();
        packets.Add((ordinary.Opcode, ordinary.Payload));
        return new(packets);
    }

    private static Net7ChatSubmission Control(uint session, string input)
    {
        // Channel names may contain spaces. Quoted arguments retain that spelling.
        var args = System.Text.RegularExpressions.Regex.Matches(input, "\"([^\"]*)\"|([^\\s]+)")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();
        if (args.Length == 0) return new([], "Usage: /chat <command> [player] [channel] [value]");
        string name = args[0];
        bool channelCommand = name == "channel";
        bool listCommand = name == "list";
        string? selectedChannel = null;
        if (channelCommand)
        {
            selectedChannel = args.ElementAtOrDefault(1);
            args = args.Skip(2).ToArray();
            name = args.ElementAtOrDefault(0) ?? "";
        }
        else if (listCommand)
        {
            args = args.Skip(1).ToArray();
            name = args.ElementAtOrDefault(0) ?? "";
        }
        if (name == "help") return new([], "Chat: /chat channel <channel> <say|create|remove|enter|exit|invite|uninvite|ban|unban|gag|ungag|owner|unowner|kick|access|password|members> [arguments]; /chat list <friends|ignores|channels|all_channels>; /chat <tell|friend|unfriend|ignore|unignore|all> [arguments].");
        if (name == "tell" && !channelCommand && !listCommand)
        {
            var tell = input.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            var privatePlan = Net7SocialChatPolicy.PlanPrivateSend(session, tell.ElementAtOrDefault(1) ?? "", tell.ElementAtOrDefault(2) ?? "", Net7PrivateChatProducer.ChatTell, []);
            return new(privatePlan.ShouldSend ? [(privatePlan.Opcode, privatePlan.Payload)] : [], privatePlan.LocalError, privatePlan);
        }
        if (channelCommand && name == "say")
        {
            var channel = Net7SocialChatPolicy.PlanSelectedMonitoredChannelSend(session, selectedChannel, string.Join(' ', args.Skip(1)));
            return new(channel.ShouldSend ? [(channel.Opcode, channel.Payload)] : [], channel.LocalError);
        }
        Net7ClientChatRequestKind? mapped = name switch
        {
            "all" => Net7ClientChatRequestKind.ChatAll,
            "create" => Net7ClientChatRequestKind.CreateChannel, "remove" => Net7ClientChatRequestKind.RemoveChannel,
            "enter" => Net7ClientChatRequestKind.EnterChannel, "exit" => Net7ClientChatRequestKind.ExitChannel,
            "friend" => Net7ClientChatRequestKind.Friend, "unfriend" => Net7ClientChatRequestKind.Unfriend,
            "ignore" => Net7ClientChatRequestKind.Ignore, "unignore" => Net7ClientChatRequestKind.Unignore,
            "invite" => Net7ClientChatRequestKind.Invite, "uninvite" => Net7ClientChatRequestKind.Uninvite,
            "ban" => Net7ClientChatRequestKind.Ban, "unban" => Net7ClientChatRequestKind.Unban,
            "gag" => Net7ClientChatRequestKind.Gag, "ungag" => Net7ClientChatRequestKind.Ungag,
            "owner" => Net7ClientChatRequestKind.Owner, "unowner" => Net7ClientChatRequestKind.Unowner,
            "kick" => Net7ClientChatRequestKind.Kick, "access" => Net7ClientChatRequestKind.Access,
            "password" => Net7ClientChatRequestKind.Password, "members" => Net7ClientChatRequestKind.ListChannelMembers,
            "ignores" => Net7ClientChatRequestKind.ListIgnores, "friends" => Net7ClientChatRequestKind.ListFriends,
            "channels" => Net7ClientChatRequestKind.ListChannels, "all_channels" => Net7ClientChatRequestKind.ListAllChannels,
            "gmgag" => Net7ClientChatRequestKind.GmGag, "gmungag" => Net7ClientChatRequestKind.GmUngag,
            _ => null,
        };
        if (mapped is not { } kind)
            return new([], "Unknown chat command: " + name);
        string? a = args.ElementAtOrDefault(1);
        bool playerOnly = kind is Net7ClientChatRequestKind.Friend or Net7ClientChatRequestKind.Unfriend or Net7ClientChatRequestKind.Ignore or Net7ClientChatRequestKind.Unignore or Net7ClientChatRequestKind.GmGag or Net7ClientChatRequestKind.GmUngag;
        bool channelOnly = kind is Net7ClientChatRequestKind.CreateChannel or Net7ClientChatRequestKind.RemoveChannel or Net7ClientChatRequestKind.EnterChannel or Net7ClientChatRequestKind.ExitChannel or Net7ClientChatRequestKind.ListChannelMembers or Net7ClientChatRequestKind.Access or Net7ClientChatRequestKind.Password;
        bool isList = kind is Net7ClientChatRequestKind.ListFriends or Net7ClientChatRequestKind.ListIgnores or Net7ClientChatRequestKind.ListChannels or Net7ClientChatRequestKind.ListAllChannels;
        if (isList != listCommand || channelCommand == (playerOnly || isList || kind == Net7ClientChatRequestKind.ChatAll))
            return new([], "Use /chat help for chat command syntax.");
        var plan = Net7SocialChatPolicy.PlanChatControlRequest(session, kind, !channelOnly ? a : null,
            selectedChannel, channelOnly ? a : kind == Net7ClientChatRequestKind.ChatAll ? string.Join(' ', args.Skip(1)) : null);
        return new(plan.ShouldSend ? [(plan.Opcode, plan.Payload)] : [], plan.ShouldSend ? null : "Missing chat command arguments.");
    }
}
