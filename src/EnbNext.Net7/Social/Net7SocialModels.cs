namespace EnbNext.Net7.Social;

public sealed record Net7GroupActionUpdate(byte Type, byte[] Data)
{
    public string? InviteText { get; init; }
}

public sealed record Net7CtaResponseUpdate(uint AvatarId, uint RequestType, byte Success);

public sealed record Net7GroupRequestPlan(
    bool ShouldSend,
    ushort Opcode,
    byte[] Payload,
    string? LocalError)
{
    public static Net7GroupRequestPlan NoSend(string? localError = null)
        => new(false, 0, Array.Empty<byte>(), localError);
}

public enum Net7GroupCtaPanelRequestType : uint
{
    Request4 = 4,
    Request5 = 5,
    Request6 = 6,
    Request7 = 7,
    Request8 = 8,
    Request9 = 9,
}

public sealed record Net7FindMemberRecord(uint ObjectId, uint NumericField, uint Race, uint Profession)
{
    // Current retail indexes PTR_s_Ter_Enf as
    // table[Profession + Race * 3].  Race is therefore the outer/row axis and
    // Profession the inner/column axis.  The table order is pinned directly
    // from the current client image.
    private static readonly string[,] CurrentClassLabels =
    {
        { "Ter/Enf", "Ter/Tra", "Ter/Sct" },
        { "Jen/Def", "Jen/Skr", "Jen/Exp" },
        { "Pro/War", "Pro/Pvt", "Pro/Sen" },
    };

    public bool HasValidClassIndex => Race <= 2 && Profession <= 2;

    // Current retail asserts then substitutes index 0 independently for an
    // out-of-range race or profession before table lookup.
    public string CurrentClassLabel => CurrentClassLabels[(int)(Race <= 2 ? Race : 0), (int)(Profession <= 2 ? Profession : 0)];
}

public sealed record Net7GroupFinderCandidate(
    uint ObjectId,
    uint NumericField,
    uint Race,
    uint Profession,
    string Name,
    string CurrentClassLabel,
    bool HasValidClassIndex);

public sealed record Net7MessageChatstreamUpdate(ushort ChatSize, byte Type, uint ShipId, IReadOnlyList<Net7PlayerNlpSegment> Segments)
{
    public string Text => string.Concat(Segments.Where(x => x.RecordType == 1).Select(x => x.Text ?? string.Empty));
}

public sealed record Net7PlayerNlpSegment(byte RecordType, byte RecordArg, byte[] Payload, string? Text);
public sealed record Net7AvatarEmoteEvent(long Sequence, uint AvatarId, System.Collections.Immutable.ImmutableArray<byte> Animations)
{
    public System.Collections.Immutable.ImmutableArray<Net7AvatarNlpCommand> Commands {get;init;}=[];
}
public sealed record Net7AvatarNlpCommand(byte Type, byte Argument, string Text);
public sealed record Net7AvatarSpeechEvent(long Sequence, uint AvatarId, ulong CreationOrdinal,
    bool ShowCaller, System.Collections.Immutable.ImmutableArray<Net7AvatarNlpCommand> Commands)
{
    public ulong? CacheLifetime {get;init;}
    public bool WorldObjectPresent {get;init;}=true;
}

public sealed record Net7ClientChatListUpdate(
    uint ListKind,
    string Context,
    IReadOnlyList<string> ArrayA,
    IReadOnlyList<string> ArrayB);

public sealed record Net7ClientChatEventUpdate(
    uint EventType,
    uint EventField,
    string Ss1,
    string Ss2,
    string Ss3,
    string Ss4,
    string Ss5,
    string Ss6,
    byte[] Blob);

public sealed record Net7ClientChatErrorUpdate(
    uint ErrorOrStatusCode,
    uint RequestCode,
    string Ss1,
    string Ss2,
    string Ss3);

public sealed record Net7FriendEntry(string Identity, string Presence)
{
    public bool IsOffline => string.Equals(Presence, "offline", StringComparison.OrdinalIgnoreCase);
}

public sealed record Net7NamedChannelList(string Context, IReadOnlyList<string> Members);

public sealed record Net7ChatPresentationEvent(byte Type, string Text, Net7ChatPresentationKind Kind)
{
    public long Sequence { get; init; }
    public EnbNext.Net7.Presentation.Net7MessageLineUpdate? ScreenLine { get; init; }
}

public enum Net7ChatPresentationKind
{
    History,
    MessageLine,
    QuickLine,
    Error,
    System,
    PrivateBeep,
    SpeakerColor,
    ScreenLine,
    ClearScreenLines,
    GuildSound,
    InterfaceSound,
    PrivateChannelNotFound,
    NamedScreenLine,
    PrivateChannelPasswordRequired,
    PrivateChannelPasswordIncorrect,
    PrivateChannelJoinEntry,
    PrivateChannelRequestCompleted,
    SelectSecureRecipient,
}

public enum Net7SelectedChatChannel
{
    Broadcast = 0,
    Local = 1,
    Guild = 2,
    Group = 3,
}

public sealed record Net7ChatPolicyContext(
    uint LocalPlayerId,
    Net7SelectedChatChannel SelectedChannel,
    bool IsInGuild,
    bool IsInGroup);

public sealed record Net7ChatCommandPlan(
    bool ShouldSend,
    ushort Opcode,
    byte[] Payload,
    string? LocalError,
    Net7ChatCommandOrigin Origin)
{
    public static Net7ChatCommandPlan NoSend(string? localError = null)
        => new(false, 0, Array.Empty<byte>(), localError, Net7ChatCommandOrigin.None);
}

public enum Net7ChatCommandOrigin
{
    None,
    SelectedChannel,
    DirectGroupSlash,
    DirectGuildSlash,
    ExactServerCommand,
    UnknownSlashFallback,
}

public sealed record Net7GuildMessagePresentation(
    uint MessageType,
    string Sound,
    string Template,
    string FormattedText,
    Net7GuildMessagePresentationKind Kind,
    IReadOnlyList<string> Lines);

public enum Net7GuildMessagePresentationKind
{
    OrdinaryGuildMessage,
    SecondaryList,
    GuildRoster,
    GuildWindowSuppressed,
}

public sealed record Net7GroupUiPresentationState(bool FormationVisualEnabled, IReadOnlySet<uint> HighlightedAvatarIds);

public enum Net7ExactServerChatCommand
{
    ResetShip,
    FounderEnable,
    GuildListAll,
    GuildRecruit,
    GuildTag,
    GuildMessage,
    GuildStats,
    GuildCreate,
    GuildDisband,
    GuildPublicStatsOn,
    GuildPublicStatsOff,
    GuildLeave,
    GuildRemove,
    GuildPromote,
    GuildDemote,
    GuildContributions,
}

public enum Net7PrivateChatProducer
{
    SelectedSecure,
    SelectedReply,
    Tell,
    TellAlias,
    Reply,
    ReplyAlias,
    ChatTell,
}

public enum Net7ClientChatRequestKind
{
    ChatAll = 3,
    CreateChannel = 4,
    RemoveChannel = 5,
    EnterChannel = 6,
    ExitChannel = 7,
    Friend = 8,
    Unfriend = 9,
    Ignore = 10,
    Unignore = 11,
    Invite = 12,
    Uninvite = 13,
    Ban = 14,
    Unban = 15,
    Gag = 16,
    Ungag = 17,
    Owner = 18,
    Unowner = 19,
    Kick = 20,
    Access = 21,
    Password = 22,
    ListIgnores = 23,
    ListFriends = 24,
    ListChannels = 25,
    ListAllChannels = 26,
    ListChannelMembers = 27,
    GmGag = 31,
    GmUngag = 32,
}

public sealed record Net7ClientChatRequestPlan(
    bool ShouldSend,
    ushort Opcode,
    byte[] Payload,
    string? LocalError)
{
    public static Net7ClientChatRequestPlan NoSend(string? localError = null)
        => new(false, 0, Array.Empty<byte>(), localError);
}

public sealed record Net7PrivateChatPlan(
    bool ShouldSend,
    ushort Opcode,
    byte[] Payload,
    string? TargetIdentity,
    bool CreateLocalEcho,
    bool PromoteSecureTarget,
    Net7PrivateChatProducer Producer,
    string? LocalError,
    bool OpenPrivateUi = false)
{
    public static Net7PrivateChatPlan NoSend(
        string? localError,
        Net7PrivateChatProducer producer = Net7PrivateChatProducer.SelectedSecure,
        string? targetIdentity = null,
        bool promoteSecureTarget = false,
        bool openPrivateUi = false)
        => new(false, 0, Array.Empty<byte>(), targetIdentity, false, promoteSecureTarget, producer, localError, openPrivateUi);
}

public sealed record Net7PrivateFriendActionPlan(
    bool ShouldOpenPrivateUi,
    string? TargetIdentity,
    bool PromoteSecureTarget,
    string? LocalError);

public sealed record Net7GuildRosterEntry(string Text, string Identity, bool IsOffline)
{
    public string RankName { get; init; } = "";
    public string Status { get; init; } = "";

    // 0081D290 -> 004FB810: first whitespace-delimited (or quoted) token,
    // followed by the remaining rank/status text. 0081AEB0 sorts online first.
    public static Net7GuildRosterEntry Parse(string line)
    {
        string text = line.TrimStart();
        int end = text.StartsWith('"') ? text.IndexOf('"', 1) : text.IndexOfAny([' ', '\t', '\r', '\n']);
        bool quoted = text.StartsWith('"');
        if (end < 0) end = text.Length;
        string name = quoted ? text.Substring(1, Math.Max(0, end - 1)) : text[..end];
        string status = text[Math.Min(text.Length, end + (quoted ? 1 : 0))..].TrimStart();
        bool offline = status.Contains("(offline)", StringComparison.Ordinal);
        // 0081C930 searches for the closing quote from index 1, then takes
        // the interior; the status also retains location/offline decorations.
        int rankEnd = status.IndexOf('"', 1 < status.Length ? 1 : status.Length);
        string rank = status.StartsWith('"') && rankEnd > 0 ? status[1..rankEnd] : "";
        return new(line, name, offline) { Status = status, RankName = rank };
    }
}

public sealed record Net7GuildReplyPlan(bool ShouldSend, ushort Opcode, byte[] Payload)
{
    public static Net7GuildReplyPlan NoSend() => new(false, 0, Array.Empty<byte>());
}

public sealed record Net7GuildControlAvailability(
    bool Stats,
    bool Contributions,
    bool Leave,
    bool Recruit,
    bool RemoveBasePermission,
    bool Message,
    bool Disband,
    bool Tag,
    bool MakePublic,
    bool MakePrivate,
    bool CanPromoteSelected,
    bool CanRemoveSelected,
    bool CanDemoteSelected);

public enum Net7GuildSecondarySubview
{
    First,
    Second,
}
