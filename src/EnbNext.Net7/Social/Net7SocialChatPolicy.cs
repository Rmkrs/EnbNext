using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Social;

/// <summary>Current retail-compatible client-side routing policy. It plans bytes only; generic sector transport remains Net7Session-owned.</summary>
public static class Net7SocialChatPolicy
{
    public static Net7ChatCommandPlan PlanSelectedChannel(Net7ChatPolicyContext context, string? text)
    {
        if (string.IsNullOrEmpty(text))
            return Net7ChatCommandPlan.NoSend();

        // Selected Guild/Group chat fails locally. Retail adds the
        // exact local message and does not queue 0x0033; this is not an
        // exceptional protocol state. Direct /g and /gu are separate branches.
        if (context.SelectedChannel == Net7SelectedChatChannel.Guild && !context.IsInGuild)
            return Net7ChatCommandPlan.NoSend("Not in a guild.");
        if (context.SelectedChannel == Net7SelectedChatChannel.Group && !context.IsInGroup)
            return Net7ChatCommandPlan.NoSend("Not in a group.");

        Net7ChatCommandType type = context.SelectedChannel switch
        {
            Net7SelectedChatChannel.Broadcast => Net7ChatCommandType.EntireSector,
            Net7SelectedChatChannel.Local => Net7ChatCommandType.LocalArea,
            Net7SelectedChatChannel.Guild => Net7ChatCommandType.Guild,
            Net7SelectedChatChannel.Group => Net7ChatCommandType.Group,
            _ => throw new ArgumentOutOfRangeException(nameof(context), "Unsupported selected chat channel."),
        };
        return BuildPlayerCommand(context.LocalPlayerId, type, text, Net7ChatCommandOrigin.SelectedChannel);
    }

    /// <summary>Models only the two direct slash forms proven to construct 0x0033 themselves. They deliberately bypass selected-channel membership guards.</summary>
    public static Net7ChatCommandPlan PlanDirectGroupOrGuildSlash(Net7ChatPolicyContext context, string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Current client quirk: /gu uses its bounded ASCII
        // case-insensitive compare, while /g is matched by literal '/', 'g',
        // ' ' byte checks. These direct branches also do not re-run the
        // selected-channel membership or post-prefix empty-text guards.
        if (input.StartsWith("/gu ", StringComparison.OrdinalIgnoreCase))
            return BuildDirectPlayerCommand(context.LocalPlayerId, Net7ChatCommandType.Guild, input[4..], Net7ChatCommandOrigin.DirectGuildSlash);
        if (input.StartsWith("/g ", StringComparison.Ordinal))
            return BuildDirectPlayerCommand(context.LocalPlayerId, Net7ChatCommandType.Group, input[3..], Net7ChatCommandOrigin.DirectGroupSlash);
        return Net7ChatCommandPlan.NoSend();
    }

    /// <summary>Retail compatibility rule after the slash parser has already classified an input as unrecognized.</summary>
    public static Net7ChatCommandPlan PlanUnknownSlashFallback(Net7ChatPolicyContext context, string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("Unknown-slash fallback requires slash-prefixed input.", nameof(input));
        Net7ChatCommandPlan selected = PlanSelectedChannel(context, input);
        return selected with { Origin = Net7ChatCommandOrigin.UnknownSlashFallback };
    }

    public static Net7ChatCommandPlan PlanExactServerCommand(Net7ChatPolicyContext context, Net7ExactServerChatCommand command, string? arg1 = null, string? arg2 = null)
    {
        (Net7ChatCommandType type, string text) = command switch
        {
            Net7ExactServerChatCommand.ResetShip => (Net7ChatCommandType.EntireSector, "/resetship"),
            Net7ExactServerChatCommand.FounderEnable => (Net7ChatCommandType.LocalArea, "/founder_enable"),
            Net7ExactServerChatCommand.GuildListAll => (Net7ChatCommandType.LocalArea, "/gc list all"),
            Net7ExactServerChatCommand.GuildRecruit => (Net7ChatCommandType.LocalArea, $"/gc recruit {Require(arg1, nameof(arg1))}"),
            Net7ExactServerChatCommand.GuildTag => (Net7ChatCommandType.LocalArea, $"/gc tag {Require(arg1, nameof(arg1))} \"{Require(arg2, nameof(arg2))}\""),
            Net7ExactServerChatCommand.GuildMessage => (Net7ChatCommandType.LocalArea, $"/gc message {Require(arg1, nameof(arg1))}"),
            Net7ExactServerChatCommand.GuildStats => (Net7ChatCommandType.LocalArea, $"/gc stats {Require(arg1, nameof(arg1))}"),
            Net7ExactServerChatCommand.GuildCreate => (Net7ChatCommandType.LocalArea, $"/gc create {Require(arg1, nameof(arg1))}"),
            Net7ExactServerChatCommand.GuildDisband => (Net7ChatCommandType.LocalArea, "/gc disband"),
            Net7ExactServerChatCommand.GuildPublicStatsOn => (Net7ChatCommandType.LocalArea, "/gc publicstats on"),
            Net7ExactServerChatCommand.GuildPublicStatsOff => (Net7ChatCommandType.LocalArea, "/gc publicstats off"),
            Net7ExactServerChatCommand.GuildLeave => (Net7ChatCommandType.LocalArea, "/gc leave"),
            Net7ExactServerChatCommand.GuildRemove => (Net7ChatCommandType.LocalArea, $"/gc remove {Require(arg1, nameof(arg1))}"),
            Net7ExactServerChatCommand.GuildPromote => (Net7ChatCommandType.LocalArea, $"/gc promote {Require(arg1, nameof(arg1))}"),
            Net7ExactServerChatCommand.GuildDemote => (Net7ChatCommandType.LocalArea, $"/gc demote {Require(arg1, nameof(arg1))}"),
            Net7ExactServerChatCommand.GuildContributions => (Net7ChatCommandType.LocalArea, $"/gc contributions {Require(arg1, nameof(arg1))}"),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        return BuildPlayerCommand(context.LocalPlayerId, type, text, Net7ChatCommandOrigin.ExactServerCommand);
    }

    public static Net7ChatCommandPlan PlanGroupDialogSignal(Net7ChatPolicyContext context, bool flag, string owner, string dialogText)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(dialogText);
        int firstSpace = owner.IndexOf(' ');
        string ownerTail = firstSpace >= 0 ? owner[(firstSpace + 1)..] : owner;
        string text = $"<S>{(flag ? 1 : 0)}{ownerTail}`{dialogText}";
        return BuildPlayerCommand(context.LocalPlayerId, Net7ChatCommandType.Group, text, Net7ChatCommandOrigin.ExactServerCommand);
    }

    public static Net7ClientChatRequestPlan PlanSelectedRadioChannelSend(
        uint startSessionValue,
        string? selectedRadioChannel,
        string? message)
        => PlanNamedChannelSend(
            startSessionValue,
            selectedRadioChannel,
            message,
            "Not subscribed to any channels.",
            null);

    public static Net7ClientChatRequestPlan PlanSelectedMonitoredChannelSend(
        uint startSessionValue,
        string? selectedMonitoredChannel,
        string? message)
        => PlanNamedChannelSend(
            startSessionValue,
            selectedMonitoredChannel,
            message,
            "Not monitoring any public channels.",
            null);

    public static Net7ClientChatRequestPlan PlanNamedChannelAlias(
        uint startSessionValue,
        string alias,
        string? message,
        string? currentRadioChannel = null,
        bool channelEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(alias);

        // The current retail parser uses plain _strncmp for these
        // four-byte aliases. Unlike /gu, this path is case-sensitive.
        string? channel = alias switch
        {
            "/gen" => "General",
            "/ooc" => "OOC",
            "/mkt" => "Market",
            "/new" => "New Players",
            "/jen" => "Jenquai",
            "/ter" => "Terran",
            "/pro" => "Progen",
            "/exp" => "Explorers",
            "/tra" => "Tradesmen",
            "/war" => "Warriors",
            "/sen" => "Sentinels",
            "/def" => "Defenders",
            "/enf" => "Enforcers",
            "/rad" => currentRadioChannel,
            _ => null,
        };

        if (channel is null && alias != "/rad")
            return Net7ClientChatRequestPlan.NoSend();
        if (alias == "/rad" && string.IsNullOrEmpty(channel))
            return Net7ClientChatRequestPlan.NoSend("Not monitoring a private channel.");

        return PlanNamedChannelSend(
            startSessionValue,
            channel,
            message,
            "Not monitoring that channel.",
            channelEnabled || alias == "/rad" ? null : "Not monitoring that channel.");
    }

    public static Net7ClientChatRequestPlan PlanChatControlRequest(
        uint startSessionValue,
        Net7ClientChatRequestKind kind,
        string? player = null,
        string? channel = null,
        string? value = null)
    {
        static bool Has(string? text) => !string.IsNullOrEmpty(text);

        (uint Code, string Arg1, string Arg2, string Arg3)? request = kind switch
        {
            Net7ClientChatRequestKind.ChatAll when Has(value) => (3, string.Empty, string.Empty, value!),
            // Retail 005EA370 sends request 4 with all three strings empty when
            // the player chooses New; the server allocates the channel number.
            Net7ClientChatRequestKind.CreateChannel => (4, string.Empty, channel ?? string.Empty, string.Empty),
            Net7ClientChatRequestKind.RemoveChannel when Has(channel) => (5, string.Empty, channel!, string.Empty),
            Net7ClientChatRequestKind.EnterChannel when channel is not null => (6, string.Empty, channel, value ?? string.Empty),
            Net7ClientChatRequestKind.ExitChannel when Has(channel) => (7, string.Empty, channel!, string.Empty),
            // Target buttons591A30/5DAB50 send even a materialized empty
            // Owner. Null still means no supplied recipient.
            Net7ClientChatRequestKind.Friend when player is not null => (8, player, string.Empty, string.Empty),
            Net7ClientChatRequestKind.Unfriend when Has(player) => (9, player!, string.Empty, string.Empty),
            Net7ClientChatRequestKind.Ignore when player is not null => (10, player, string.Empty, string.Empty),
            Net7ClientChatRequestKind.Unignore when Has(player) => (11, player!, string.Empty, string.Empty),
            Net7ClientChatRequestKind.Invite when Has(player) && Has(channel) => (12, player!, channel!, string.Empty),
            Net7ClientChatRequestKind.Uninvite when Has(player) && Has(channel) => (13, player!, channel!, string.Empty),
            Net7ClientChatRequestKind.Ban when Has(player) && Has(channel) => (14, player!, channel!, string.Empty),
            Net7ClientChatRequestKind.Unban when Has(player) && Has(channel) => (15, player!, channel!, string.Empty),
            Net7ClientChatRequestKind.Gag when Has(player) && Has(channel) => (16, player!, channel!, string.Empty),
            Net7ClientChatRequestKind.Ungag when Has(player) && Has(channel) => (17, player!, channel!, string.Empty),
            Net7ClientChatRequestKind.Owner when Has(player) && Has(channel) => (18, player!, channel!, string.Empty),
            Net7ClientChatRequestKind.Unowner when Has(player) && Has(channel) => (19, player!, channel!, string.Empty),
            Net7ClientChatRequestKind.Kick when Has(player) && Has(channel) => (20, player!, channel!, string.Empty),
            Net7ClientChatRequestKind.Access when Has(channel) && Has(value) => (21, string.Empty, channel!, value!),
            Net7ClientChatRequestKind.Password when Has(channel) && Has(value) => (22, string.Empty, channel!, value!),
            Net7ClientChatRequestKind.ListIgnores => (23, string.Empty, string.Empty, string.Empty),
            Net7ClientChatRequestKind.ListFriends => (24, string.Empty, string.Empty, string.Empty),
            Net7ClientChatRequestKind.ListChannels => (25, string.Empty, string.Empty, string.Empty),
            Net7ClientChatRequestKind.ListAllChannels => (26, string.Empty, string.Empty, string.Empty),
            Net7ClientChatRequestKind.ListChannelMembers when Has(channel) => (27, string.Empty, channel!, string.Empty),
            Net7ClientChatRequestKind.GmGag when Has(player) => (31, player!, string.Empty, string.Empty),
            Net7ClientChatRequestKind.GmUngag when Has(player) => (32, player!, string.Empty, string.Empty),
            _ => null,
        };

        if (request is not { } exact)
            return Net7ClientChatRequestPlan.NoSend();

        return new Net7ClientChatRequestPlan(
            true,
            0x00A3,
            Net7GameplayWire.BuildClientChatRequest(
                startSessionValue, exact.Code, exact.Arg1, exact.Arg2, exact.Arg3, ReadOnlySpan<byte>.Empty),
            null);
    }

    private static Net7ClientChatRequestPlan PlanNamedChannelSend(
        uint startSessionValue,
        string? channel,
        string? message,
        string missingChannelError,
        string? disabledChannelError)
    {
        if (string.IsNullOrEmpty(channel))
            return Net7ClientChatRequestPlan.NoSend(missingChannelError);
        if (disabledChannelError is not null)
            return Net7ClientChatRequestPlan.NoSend(disabledChannelError);
        if (string.IsNullOrEmpty(message))
            return Net7ClientChatRequestPlan.NoSend("No message to send.");
        return new Net7ClientChatRequestPlan(
            true,
            0x00A3,
            Net7GameplayWire.BuildClientChatRequest(startSessionValue, 0, string.Empty, channel, message, ReadOnlySpan<byte>.Empty),
            null);
    }

    public static Net7PrivateChatPlan PlanPrivateSend(
        uint startSessionValue,
        string target,
        string message,
        Net7PrivateChatProducer producer,
        ReadOnlySpan<byte> finalizedPlayerNlp)
    {
        bool promoteSecure = producer is Net7PrivateChatProducer.Tell or Net7PrivateChatProducer.TellAlias;
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrEmpty(message))
        {
            string? error = producer switch
            {
                Net7PrivateChatProducer.Tell => "Usage: /tell <player> <message>",
                Net7PrivateChatProducer.TellAlias => "Usage: /t <player> <message>",
                _ => null,
            };
            return Net7PrivateChatPlan.NoSend(error, producer, string.IsNullOrWhiteSpace(target) ? null : target, promoteSecure);
        }

        bool richSlashProducer = producer is Net7PrivateChatProducer.Tell
            or Net7PrivateChatProducer.TellAlias
            or Net7PrivateChatProducer.Reply
            or Net7PrivateChatProducer.ReplyAlias;
        if (richSlashProducer && finalizedPlayerNlp.IsEmpty)
        {
            // The dedicated /tell and /reply paths only construct
            // their Code-1 A3 after resolving the local avatar/PlayerNLP source.
            // /tell still promotes the explicit peer into Secure state afterward.
            return Net7PrivateChatPlan.NoSend(null, producer, target, promoteSecure);
        }

        bool zeroBlob = producer == Net7PrivateChatProducer.ChatTell;
        byte[] blob = zeroBlob ? Array.Empty<byte>() : finalizedPlayerNlp.ToArray();
        byte[] payload = Net7GameplayWire.BuildClientChatRequest(startSessionValue, 1, target, string.Empty, message, blob);
        bool localEcho = producer != Net7PrivateChatProducer.ChatTell;
        return new Net7PrivateChatPlan(true, 0x00A3, payload, target, localEcho, promoteSecure, producer, null);
    }

    public static Net7PrivateChatPlan PlanReply(uint startSessionValue, string? replyIdentity, string message, Net7PrivateChatProducer producer, ReadOnlySpan<byte> finalizedPlayerNlp)
    {
        if (producer is not Net7PrivateChatProducer.Reply and not Net7PrivateChatProducer.ReplyAlias and not Net7PrivateChatProducer.SelectedReply)
            throw new ArgumentOutOfRangeException(nameof(producer));
        if (string.IsNullOrEmpty(replyIdentity))
        {
            string error = producer == Net7PrivateChatProducer.SelectedReply
                ? "No one to reply to."
                : "Unable to reply - nothing to reply to.";
            return Net7PrivateChatPlan.NoSend(error, producer);
        }
        if (string.IsNullOrEmpty(message))
        {
            bool openUi = producer is Net7PrivateChatProducer.Reply or Net7PrivateChatProducer.ReplyAlias;
            return Net7PrivateChatPlan.NoSend(null, producer, replyIdentity, openPrivateUi: openUi);
        }
        return PlanPrivateSend(startSessionValue, replyIdentity, message, producer, finalizedPlayerNlp);
    }

    public static byte[] BuildPresenceRequest(uint startSessionValue, bool restricted)
        => Net7GameplayWire.BuildClientChatRequest(startSessionValue, restricted ? 28u : 29u, string.Empty, string.Empty, string.Empty, ReadOnlySpan<byte>.Empty);

    public static byte[] BuildPresentationActivationRequest(uint startSessionValue)
        => Net7GameplayWire.BuildClientChatRequest(startSessionValue, 30u, string.Empty, string.Empty, string.Empty, ReadOnlySpan<byte>.Empty);

    private static Net7ChatCommandPlan BuildPlayerCommand(uint localPlayerId, Net7ChatCommandType type, string text, Net7ChatCommandOrigin origin)
    {
        if (string.IsNullOrEmpty(text))
            return Net7ChatCommandPlan.NoSend();
        return BuildDirectPlayerCommand(localPlayerId, type, text, origin);
    }

    private static Net7ChatCommandPlan BuildDirectPlayerCommand(uint localPlayerId, Net7ChatCommandType type, string text, Net7ChatCommandOrigin origin)
        => new(true, 0x0033, Net7GameplayWire.BuildPlayerCommand(localPlayerId, type, text), null, origin);

    private static string Require(string? value, string name)
        => string.IsNullOrEmpty(value) ? throw new ArgumentException("Required command argument is empty.", name) : value;
}
