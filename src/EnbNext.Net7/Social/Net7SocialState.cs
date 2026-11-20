using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Social;

/// <summary>Integrated current-client social/chat presentation state. Server-authored AuxData remains authoritative for guild/group membership.</summary>
public sealed partial class Net7SocialState
{
    public void ReportQuickMessageSaved(int slot, string fileName)
    {
        if (slot is < 1 or > 9) throw new ArgumentOutOfRangeException(nameof(slot));
        // Retail 0065E250 adds type 0 locally after persisting the MSG entry.
        ApplyBaseMessage(new(0, $"Quick message MSG_{slot} has been saved in {fileName}."));
    }
    public const int ChatHistoryCapacity = 200;
    public const int SecureTargetCapacity = 3;
    public const int RecentPrivatePeerCapacity = 5;
    public Guid StreamId { get; private set; } = Guid.NewGuid();
    private long _messageSequence;
    private long _eventSequence;
    private long _avatarEmoteSequence;
    private readonly List<Net7AvatarEmoteEvent> _avatarEmotes = [];
    public IReadOnlyList<Net7AvatarEmoteEvent> AvatarEmotes => _avatarEmotes;
    private long _avatarSpeechSequence;
    private readonly List<Net7AvatarSpeechEvent> _avatarSpeech = [];
    public IReadOnlyList<Net7AvatarSpeechEvent> AvatarSpeech => _avatarSpeech;
    private readonly Func<uint, ulong?> _resolveAvatar;
    private readonly Func<uint,ulong?>? _resolveAvatarCache;
    private readonly Func<string, uint?> _resolveCaller;
    public sealed record GroupSymbol(long Sequence, string Member, string Asset, bool Context);
    private readonly List<GroupSymbol> _groupSymbols = [];
    public IReadOnlyList<GroupSymbol> GroupSymbols => _groupSymbols;
    private long _groupSymbolSequence;
    public long ChatRevision { get; private set; }
    public event Action<string,byte>? SpeakerPresented;

    private readonly Func<uint, string?> _resolveObjectName;
    private readonly Func<uint, string?> _resolveFinderName;
    private readonly Func<uint?> _localPlayerId;
    private readonly List<Net7MessageState> _messages = [];
    private readonly List<Net7ChatPresentationEvent> _presentationEvents = [];
    private readonly Dictionary<string, Net7FriendEntry> _friends = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _ignoredRows = [];
    public IReadOnlyList<string> IgnoredRows => _ignoredRows;
    public long IgnoredRevision { get; private set; }
    private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _secureTargets = [];
    private readonly List<string> _recentPrivatePeers = [];
    private readonly Dictionary<string, IReadOnlyList<string>> _channelMembers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _channelMemberRevisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<uint> _highlightedGroupAvatarIds = [];

    public Net7SocialState(Func<uint, string?>? resolveObjectName = null, Func<uint?>? localPlayerId = null,
        Func<uint, ulong?>? resolveAvatar = null, Func<string, uint?>? resolveCaller = null,
        Func<uint, string?>? resolveFinderName = null,Func<uint,ulong?>? resolveAvatarCache=null)
    {
        _resolveObjectName = resolveObjectName ?? (_ => null);
        _resolveFinderName = resolveFinderName ?? _resolveObjectName;
        _localPlayerId = localPlayerId ?? (() => null);
        _resolveAvatar = resolveAvatar ?? (_ => null);
        _resolveAvatarCache=resolveAvatarCache;
        _resolveCaller = resolveCaller ?? (_ => null);
    }

    public IReadOnlyList<Net7MessageState> Messages => _messages;
    public IReadOnlyList<Net7ChatPresentationEvent> PresentationEvents => _presentationEvents;
    public IReadOnlyDictionary<string, Net7FriendEntry> Friends => _friends;
    public IReadOnlySet<string> Ignored => _ignored;
    public IReadOnlyList<string> SecureTargets => _secureTargets;
    public IReadOnlyList<string> RecentPrivatePeers => _recentPrivatePeers;
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ChannelMembers => _channelMembers;
    public IReadOnlyDictionary<string, long> ChannelMemberRevisions => _channelMemberRevisions;
    public IReadOnlyList<string> ActiveChannels { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> CurrentChannels { get; private set; } = Array.Empty<string>();
    public string? LastCreatedChannel { get; private set; }
    public long CreatedChannelRevision { get; private set; }
    public string PrivateChannel { get; private set; } = "";
    public bool PrivateChannelOwner { get; private set; }
    public string? ReplyIdentity { get; private set; }
    public Net7GroupActionUpdate? GroupInvite { get; private set; }
    public long GroupInviteRevision { get; private set; }
    public Net7CtaResponseUpdate? LastUnhandledCtaResponse { get; private set; }
    public bool GroupFormationVisualEnabled { get; private set; }
    public IReadOnlySet<uint> HighlightedGroupAvatarIds => _highlightedGroupAvatarIds;
    public IReadOnlyList<Net7GroupFinderCandidate> GroupFinderCandidates { get; private set; } = Array.Empty<Net7GroupFinderCandidate>();
    // 00739500 admits responses only while the finder child is active. Keep
    // the existing diagnostic projection separately from the UI arrival order.
    public bool GroupFinderWindowActive { get; private set; }
    public long GroupFinderRevision { get; private set; }
    public IReadOnlyList<Net7GroupFinderCandidate> GroupFinderRows { get; private set; } = [];
    public void SetGroupFinderWindow(bool active)
    {
        GroupFinderWindowActive = active;
        GroupFinderRows = [];
        GroupFinderRevision++;
    }
    public Net7ClientChatErrorUpdate? LastChatError { get; private set; }
    public bool RestrictedStatus { get; private set; }
    public long FriendsRevision { get; private set; }
    public bool IgnoreWindowActive { get; private set; }
    public bool ChannelMembersWindowActive { get; private set; }
    private string _membersWindowChannel = "";
    public void SetChatListWindow(Guid stream, bool ignore, bool members, string channel)
    {
        if (stream != StreamId) return;
        if (ignore && !IgnoreWindowActive) { _ignoredRows.Clear(); IgnoredRevision++; ChatRevision++; }
        if (members && (!ChannelMembersWindowActive || channel != _membersWindowChannel))
        {
            _channelMembers[channel] = [];
            _channelMemberRevisions[channel] = _channelMemberRevisions.GetValueOrDefault(channel) + 1;
            ChatRevision++;
        }
        IgnoreWindowActive = ignore; ChannelMembersWindowActive = members; _membersWindowChannel = members ? channel : "";
    }
    public bool FriendsWindowActive { get; private set; }
    public void SetFriendsWindow(bool active)
    {
        if (active && !FriendsWindowActive) { _friends.Clear(); FriendsRevision++; ChatRevision++; }
        FriendsWindowActive = active;
    }
    public Net7GuildMessagePresentation? GuildPresentation { get; private set; }
    public IReadOnlyList<string> GuildRosterLines { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<Net7GuildRosterEntry> GuildRosterEntries { get; private set; } = Array.Empty<Net7GuildRosterEntry>();
    public IReadOnlyList<string> GuildSecondaryListLines { get; private set; } = Array.Empty<string>();
    public long GuildDisplayRevision { get; private set; }
    public IReadOnlyList<string> GuildSecondaryFirstLines { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> GuildSecondarySecondLines { get; private set; } = Array.Empty<string>();
    public bool GuildWindowActive { get; set; }
    public void SetGuildWindow(bool active)
    {
        if (active && !GuildWindowActive)
        {
            GuildRosterLines = Array.Empty<string>();
            GuildRosterEntries = Array.Empty<Net7GuildRosterEntry>();
            GuildSecondaryFirstLines = GuildSecondarySecondLines = GuildSecondaryListLines = Array.Empty<string>();
            GuildSecondarySubview = Net7GuildSecondarySubview.First;
        }
        GuildWindowActive = active;
    }
    public Net7GuildSecondarySubview GuildSecondarySubview { get; set; }

    public void ApplyBaseMessage(Net7MessageState raw, bool suppressPrefix = false)
    {
        ArgumentNullException.ThrowIfNull(raw);
        // Current retail AddChatMessage drops a null/empty incoming C string before
        // any type-specific prefix, presentation, log or history processing.
        if (string.IsNullOrEmpty(raw.Text))
            return;
        if (raw.Type > 23)
            throw new InvalidDataException($"0x001D message type {raw.Type} is outside the current 0..23 domain.");

        if (raw.Type == 22)
        {
            AddPresentationEvent(new(raw.Type, raw.Text, Net7ChatPresentationKind.QuickLine));
            return;
        }

        string normalized = suppressPrefix ? raw.Text : raw.Type switch
        {
            0 or 17 => "COMPUTER: " + raw.Text,
            1 or 18 or 20 => "OUTDATED MESSAGE TYPE: " + raw.Text,
            6 => "TIP: " + raw.Text,
            19 => "SYSTEM: " + raw.Text,
            21 => "WARNING: " + raw.Text,
            _ => raw.Text,
        };

        if (raw.Type is 6 or 21)
            AddPresentationEvent(new(raw.Type, raw.Text, Net7ChatPresentationKind.MessageLine));
        AddHistory(raw with { Text = normalized });
    }

    public void ApplyGroupAction(Net7GroupActionUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Type == 1)
        {
            // 00736930/00588A70: an invitation that arrives while another is being answered waits its turn (GROUP-D13).
            if (GroupInvite is not null) { _queuedGroupInvites.Add(update); return; }
            GroupInvite = update;
            GroupInviteRevision++;
        }
    }
    private readonly List<Net7GroupActionUpdate> _queuedGroupInvites = [];

    /// <summary>The answered invitation is done; 00588640 then opens the next queued one.</summary>
    public void ClearGroupInvite()
    {
        GroupInvite = null;
        if (_queuedGroupInvites.Count == 0) return;
        GroupInvite = _queuedGroupInvites[0]; _queuedGroupInvites.RemoveAt(0);
        GroupInviteRevision++;
    }

    public void ApplyCtaResponse(Net7CtaResponseUpdate update,bool isAvatarViewActive=false,bool isGroupLeader=false)
    {
        ArgumentNullException.ThrowIfNull(update);
        //73DAA0 returns before dispatch (including its diagnostic branch) when
        //744B60 identifies the active avatar view. Retained station != active.
        if(isAvatarViewActive)return;
        switch (update.RequestType)
        {
            case 13:
                break;
            case 14:
                GroupFormationVisualEnabled = false;
                break;
            case 15:
                GroupFormationVisualEnabled = true;
                break;
            case 17:
                _highlightedGroupAvatarIds.Add(update.AvatarId);
                break;
            default:
                // Retail 0073DAA0 reports an assertion, then returns normally.
                // Preserve the diagnostic without terminating the gameplay stream.
                LastUnhandledCtaResponse = update;
                break;
        }
        ObserveGroupAttention(update,isGroupLeader);
        // Current processor deliberately ignores Success.
    }

    public void ApplyFindMember(IReadOnlyList<Net7FindMemberRecord> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        uint? local = _localPlayerId();
        var candidates = new List<Net7GroupFinderCandidate>(rows.Count);
        foreach (Net7FindMemberRecord row in rows)
        {
            // 00599FA0 excludes our ship when rebuilding the displayed list.
            if (local.HasValue && row.ObjectId == local.Value)
                continue;
            // 0059A400 calls 006FF4B0 for loaded objects. Only an absent
            // object keeps Anonymous; loaded unknown/empty identities survive.
            string name = _resolveFinderName(row.ObjectId) ?? "Anonymous";
            candidates.Add(new Net7GroupFinderCandidate(
                row.ObjectId, row.NumericField, row.Race, row.Profession, name, row.CurrentClassLabel, row.HasValidClassIndex));
        }
        if (GroupFinderWindowActive)
        {
            GroupFinderRows = candidates.ToArray();
            GroupFinderRevision++;
        }
        GroupFinderCandidates = candidates
            .OrderBy(x => x.NumericField)
            .ThenBy(x => x.CurrentClassLabel, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private void PublishAvatarSpeech(uint id, bool caller, IReadOnlyList<Net7PlayerNlpSegment> segments)
    {
        // Both callers (00736F10 chatstream, 0073B150 caller NLP) already resolved
        // the avatar with this same predicate before reaching here.
        ulong? ordinal=_resolveAvatar(id),lifetime=_resolveAvatarCache?.Invoke(id);
        _avatarSpeech.Add(new(++_avatarSpeechSequence, id, ordinal??0, caller && id != _localPlayerId(),
            System.Collections.Immutable.ImmutableArray.CreateRange(segments.Select(s =>
                new Net7AvatarNlpCommand(s.RecordType, s.RecordArg, s.Text ?? ""))))
            {CacheLifetime=lifetime,WorldObjectPresent=ordinal is not null});
        if (_avatarSpeech.Count > ChatHistoryCapacity) _avatarSpeech.RemoveAt(0);
        ChatRevision++;
    }

    public void ApplyMessageChatstream(Net7MessageChatstreamUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        // 00736F10 resolves the avatar before any speech, caller or chat effects.
        if ((_resolveAvatarCache is {} cache ? cache(update.ShipId) : _resolveAvatar(update.ShipId)) is null)
            return;
        PublishAvatarSpeech(update.ShipId, update.Type is 3 or 4, update.Segments);
        // 00736F10 feeds the avatar before applying private/chat display rules.
        // Keep packets distinct so a repeated emote is a new animation, while a
        // republished snapshot cannot restart an already consumed animation.
        var animations = System.Collections.Immutable.ImmutableArray.CreateRange(update.Segments
            .Where(s => s.RecordType == 2 && s.RecordArg != 0).Select(s => s.RecordArg));
        {
            // Station actors live in the avatar cache, independently of the
            // space object's creation ordinal. Preserve the complete 736F10
            // stream, including text/mood and a terminator-only replacement.
            _avatarEmotes.Add(new(++_avatarEmoteSequence, update.ShipId, animations)
            {Commands=System.Collections.Immutable.ImmutableArray.CreateRange(update.Segments.Select(s=>
                new Net7AvatarNlpCommand(s.RecordType,s.RecordArg,s.Text??"")))});
            if (_avatarEmotes.Count > ChatHistoryCapacity) _avatarEmotes.RemoveAt(0);
            ChatRevision++;
        }
        // 004C4940 tests for a text record, not for a nonempty string.
        if (!update.Segments.Any(s => s.RecordType == 1)) return;
        // 00736F10 formats the resolved avatar's name (004C1200) without
        // testing it; an avatar with an empty name still prints and beeps.
        string peer = _resolveObjectName(update.ShipId) ?? string.Empty;
        uint? local = _localPlayerId();
        if (update.Type == 3)
        {
            if (local.HasValue && update.ShipId == local.Value)
                return;
            AddHistory(new Net7MessageState(3, $"{peer} (private): {update.Text}", update.ShipId, peer, peer));
            AddPresentationEvent(new(3, peer, Net7ChatPresentationKind.SpeakerColor));
            AddPresentationEvent(new(3, peer, Net7ChatPresentationKind.PrivateBeep));
            SetReply(peer);
            return;
        }
        // 00736F10 applies the same own-avatar exclusion and notification to
        // private and group streams; only private streams change the reply peer.
        if (update.Type == 4 && update.ShipId != local)
        {
            AddHistory(new Net7MessageState(4, $"{peer} (group): {update.Text}", update.ShipId, peer, peer));
            AddPresentationEvent(new(4, peer, Net7ChatPresentationKind.SpeakerColor));
            AddPresentationEvent(new(4, peer, Net7ChatPresentationKind.PrivateBeep));
        }
    }

    public void ApplyClientChatList(Net7ClientChatListUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        // 0073CD33: the processor only dispatches kinds 0 through 3.
        if (update.ListKind > 3) return;
        ChatRevision++;
        switch (update.ListKind)
        {
            case 0:
                // 005913E0 preserves the existing list on unequal vector sizes.
                // The closed-window chat fallback still prints ArrayA.
                if (update.ArrayA.Count != update.ArrayB.Count) break;
                FriendsRevision++;
                _friends.Clear();
                for (int i = 0; i < update.ArrayA.Count; i++)
                    _friends[update.ArrayA[i]] = new Net7FriendEntry(update.ArrayA[i], update.ArrayB[i]);
                break;
            case 1:
                _ignoredRows.Clear(); _ignoredRows.AddRange(update.ArrayA); IgnoredRevision++;
                _ignored.Clear();
                foreach (string identity in update.ArrayA)
                    _ignored.Add(identity);
                break;
            case 2:
                // 0073CC90 dispatches to the existing Members owner; 005E5D70
                // replaces its list without rejecting or reselecting by packet context.
                string membersChannel = ChannelMembersWindowActive ? _membersWindowChannel : update.Context;
                _channelMembers[membersChannel] = update.ArrayA.OrderBy(name => name, StringComparer.Ordinal).ToArray();
                _channelMemberRevisions[membersChannel] = _channelMemberRevisions.GetValueOrDefault(membersChannel) + 1;
                break;
            case 3:
                ActiveChannels = update.ArrayA.ToArray();
                break;
            case 4:
                CurrentChannels = update.ArrayA.ToArray();
                break;
        }
        // 0073CC90's chat fallback: list responses remain readable even when
        // their dedicated social window is not open.
        string? heading = update.ListKind switch { 0 => "Friends:", 1 => "Ignored:", 2 => $"Members of channel {update.Context}:", 3 => "Active channels:", 4 => "Current channels:", _ => null };
        if (heading is not null && !(update.ListKind == 0 && FriendsWindowActive) &&
            !(update.ListKind == 1 && IgnoreWindowActive) && !(update.ListKind == 2 && ChannelMembersWindowActive))
        {
            AddHistory(new(23, heading));
            if (update.ArrayA.Count == 0) AddHistory(new(23, "<none>"));
            foreach (var line in update.ArrayA) AddHistory(new(23, line));
        }
    }

    public void ApplyClientChatEvent(Net7ClientChatEventUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        // Current retail 0073B150 intercepts these before ordinary channel routing.
        string symbol = update.Ss5;
        if (update.EventType == 3 && update.Ss4.StartsWith("group", StringComparison.OrdinalIgnoreCase)
            && symbol.StartsWith("<S>", StringComparison.Ordinal) && symbol.Length >= 4
            && symbol[3] is '0' or '1' && symbol.IndexOf('`', 4) is int separator && separator >= 4)
        {
            _groupSymbols.Add(new(++_groupSymbolSequence, symbol[4..separator], symbol[(separator + 1)..], symbol[3] == '1'));
            if (_groupSymbols.Count > ChatHistoryCapacity) _groupSymbols.RemoveAt(0);
            ChatRevision++;
            return;
        }
        switch (update.EventType)
        {
            case 1:
                // Current 0073b37f/0073b3f0 selects Ss2, the display name.
                AddSystemContext($"{update.Ss2} logged in.");
                break;
            case 2:
                AddSystemContext($"{update.Ss2} logged out.");
                break;
            case 3:
            {
                string channel = StripChannelSuffix(update.Ss4);
                // Current retail 0073B150 classifies named channels before history/color routing.
                byte type = ChannelMessageType(channel);
                AddHistory(new Net7MessageState(type, type == 2 ? update.Ss5 : $"[{channel}] {update.Ss2}: {update.Ss5}", RoutingIdentity: update.Ss1, DisplayIdentity: update.Ss2, Channel: NamedChannel(type, channel)));
                PresentSpeaker(update.Ss1, type);
                break;
            }
            case 4:
            {
                if (update.Blob.Length != 0)
                {
                    if (_resolveCaller(update.Ss1) is uint caller && (_resolveAvatarCache is {} cache?cache(caller):_resolveAvatar(caller)) is not null)
                    {
                        var commands = Net7SocialWire.DecodePlayerNlp(update.Blob);
                        PublishAvatarSpeech(caller, true, commands);
                        if (caller == _localPlayerId()) return;
                        // NLP vtable00AEBAA8+C -> 004C4940 tests record presence,
                        // including a text record whose string is empty.
                        if (!commands.Any(c => c.RecordType == 1)) return;
                    }
                    // 0073B744 falls back to ordinary private text when caller or
                    // avatar lookup fails. Only the resolved avatar ID above
                    // suppresses a local echo; matching a name is not enough.
                }
                AddHistory(new Net7MessageState(3, $"{update.Ss2} (private): {update.Ss5}", RoutingIdentity: update.Ss1, DisplayIdentity: update.Ss2));
                PresentSpeaker(update.Ss1, 3);
                AddPresentationEvent(new(3, update.Ss1, Net7ChatPresentationKind.PrivateBeep));
                SetReply(update.Ss1);
                break;
            }
            case 19:
                _ignoredRows.Add(update.Ss5); IgnoredRevision++;
                _ignored.Add(update.Ss5);
                // Retail 0073B37F/0073B3C0 stores Ss2 and Ss5 for the
                // 0073BC24 printf operands; Ss6 is not used for this notice.
                AddSystemContext($"{update.Ss2} is now ignoring {update.Ss5}.");
                break;
            case 5:
                AddHistory(new(7, update.Ss5, RoutingIdentity: update.Ss1, DisplayIdentity: update.Ss2));
                PresentSpeaker(update.Ss1, 7);
                break;
            case 6:
                AddHistory(new(ChannelMessageType(StripChannelSuffix(update.Ss4)), $"(system) {update.Ss2}: {update.Ss5}", Channel: NamedChannel(ChannelMessageType(StripChannelSuffix(update.Ss4)), StripChannelSuffix(update.Ss4))));
                break;
            case 7: case 8:
            {
                // Retail 0073B150 forwards joined/left events to both the HUD
                // (005679A0) and channel membership model (005E5EF0/005E6030).
                // Keep the local membership used by chat destinations in sync.
                string channel = StripChannelSuffix(update.Ss4);
                if (channel.StartsWith("Sector", StringComparison.OrdinalIgnoreCase)) break;
                var members = _channelMembers.GetValueOrDefault(channel, Array.Empty<string>()).ToList();
                if (update.EventType == 7) members.Add(update.Ss1);
                else RemoveOrdinal(members, update.Ss1);
                _channelMembers[channel] = members.OrderBy(name => name, StringComparer.Ordinal).ToArray();
                _channelMemberRevisions[channel] = _channelMemberRevisions.GetValueOrDefault(channel) + 1;
                // 0073B150 passes packet +10 != 0 to 005679A0. This is
                // authoritative even when the local object/name is not loaded.
                if (update.EventField != 0)
                {
                    // HUD 005679A0: self join always sounds; self leave only
                    // sounds when it matches the current private channel.
                    if (update.EventType == 7)
                        AddPresentationEvent(new(0, "", Net7ChatPresentationKind.PrivateChannelRequestCompleted));
                    if (update.EventType == 7 || PrivateChannel.Equals(channel, StringComparison.Ordinal))
                        AddPresentationEvent(new(0, "success_1.wav", Net7ChatPresentationKind.InterfaceSound));
                    var channels = CurrentChannels.ToList();
                    RemoveOrdinal(channels, channel);
                    if (update.EventType == 7) channels.Add(channel);
                    CurrentChannels = channels;
                    if (update.EventType == 7 && ChannelMessageType(channel) == 13)
                    {
                        if (!PrivateChannel.Equals(channel, StringComparison.Ordinal)) PrivateChannelOwner = false;
                        PrivateChannel = channel;
                    }
                    else if (update.EventType == 8 && PrivateChannel.Equals(channel, StringComparison.Ordinal))
                    { PrivateChannel = ""; PrivateChannelOwner = false; }
                }
                ChatRevision++;
                break;
            }
            case 9:
                AddPresentationEvent(new(0, "", Net7ChatPresentationKind.PrivateChannelRequestCompleted));
                AddPresentationEvent(new(0, "success_1.wav", Net7ChatPresentationKind.InterfaceSound));
                LastCreatedChannel = StripChannelSuffix(update.Ss4);
                CreatedChannelRevision++;
                PrivateChannel = LastCreatedChannel;
                PrivateChannelOwner = true;
                if (!CurrentChannels.Contains(LastCreatedChannel, StringComparer.OrdinalIgnoreCase))
                    CurrentChannels = CurrentChannels.Append(LastCreatedChannel).ToArray();
                AddHistory(new(ChannelMessageType(StripChannelSuffix(update.Ss4)), $"[{StripChannelSuffix(update.Ss4)}] New Channel Created!"));
                break;
            case 11:
                AddHistory(new(ChannelMessageType(StripChannelSuffix(update.Ss4)), $"{update.Ss2} removed channel {StripChannelSuffix(update.Ss4)}."));
                break;
            case 12: case 13: case 14: case 15: case 16: case 17: case 18:
            {
                string verb = update.EventType switch { 12 => "invited", 13 => "uninvited", 14 => "gagged", 15 => "ungagged", 16 => "banned", 17 => "unbanned", _ => "kicked" };
                string channel = StripChannelSuffix(update.Ss4);
                AddHistory(new(ChannelMessageType(channel), $"[{channel}] {update.Ss2} {verb} {update.Ss5}."));
                // 0073B150 events 16/18 → 005E6030/005E9160: a banned or kicked player leaves the open Members list and the
                // private channel's member list (first exact match), which then rebuild (CHAT-D13).
                if (update.EventType is 16 or 18)
                    foreach (string list in new[] { ChannelMembersWindowActive ? _membersWindowChannel : "", PrivateChannel }.Distinct())
                        if (list.Length > 0 && _channelMembers.TryGetValue(list, out var rows) && rows.ToList() is var kept && kept.Remove(update.Ss5))
                        {
                            _channelMembers[list] = kept.ToArray();
                            _channelMemberRevisions[list] = _channelMemberRevisions.GetValueOrDefault(list) + 1;
                        }
                break;
            }
            case 23: case 24:
            {
                string channel = StripChannelSuffix(update.Ss4);
                bool added = update.EventType == 23;
                if (update.EventField != 0 && PrivateChannel.Equals(channel, StringComparison.Ordinal))
                    PrivateChannelOwner = added;
                string actor = string.IsNullOrEmpty(update.Ss2) ? "The system has" : update.Ss2;
                AddHistory(new(ChannelMessageType(channel), $"[{channel}] {actor} {(added ? "added" : "removed")} {update.Ss3} {(added ? "to" : "from")} the channel owner list."));
                break;
            }
            case 20:
                RemoveOrdinal(_ignoredRows, update.Ss5); IgnoredRevision++;
                _ignored.Remove(update.Ss5);
                AddSystemContext($"{update.Ss2} is no longer ignoring {update.Ss5}.");
                break;
            case 21:
                FriendsRevision++;
                _friends[update.Ss5] = new Net7FriendEntry(update.Ss5, update.Ss6);
                // 0073BCF4/0073BD46 format SS2 (actor) and SS5 (friend); SS6 is list status.
                AddSystemContext($"{update.Ss2} is now friends with {update.Ss5}.");
                break;
            case 22:
                FriendsRevision++;
                _friends.Remove(update.Ss5);
                AddSystemContext($"{update.Ss2} is no longer friends with {update.Ss5}.");
                break;
            case 25:
                // Current retail treats 25/26 as presentation-only
                // confirmations. RestrictedStatus remains client-option state
                // selected by the Code-28/29 producer and is not server-owned.
                ApplyBaseMessage(new Net7MessageState(19, "Only members of your Friends List may see if you are online."));
                break;
            case 26:
                ApplyBaseMessage(new Net7MessageState(19, "All players may see you on their Friends List."));
                break;
            case 27:
                AddSystemContext($"{update.Ss5} has been gagged by GM {update.Ss1}.");
                break;
            case 28:
                AddSystemContext($"GM {update.Ss1} has removed the gag on {update.Ss5}.");
                break;
        }
    }

    public void ApplyClientChatError(Net7ClientChatErrorUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        LastChatError = update;
        // 00567DF0 clears HUD+225 only when 0073D170 actually calls it.
        if (update.RequestCode == 6 && update.ErrorOrStatusCode != 0 &&
            (update.ErrorOrStatusCode is 10 or 12 or 13 || !update.Ss2.StartsWith("Sector", StringComparison.OrdinalIgnoreCase)))
            AddPresentationEvent(new(0, "", Net7ChatPresentationKind.PrivateChannelRequestCompleted));
        // A6 0073D170 failed request 6 outside its special cases -> HUD 00567DF0 state 4 ->
        // 00566AF0 opens CHT03 mode 1. Sector errors do not open this UI.
        if (update.RequestCode == 6 && update.ErrorOrStatusCode is not (0 or 10 or 12 or 13) &&
            !update.Ss2.StartsWith("Sector", StringComparison.OrdinalIgnoreCase))
        {
            AddPresentationEvent(new(0, "", Net7ChatPresentationKind.PrivateChannelNotFound));
            AddPresentationEvent(new(0, "cost.wav", Net7ChatPresentationKind.InterfaceSound));
        }
        // Banned join: 00567DF0 mode 10 clears the selected channel and
        // opens ordinary CHT03 again (HUD state 3), with cost.wav.
        if (update.RequestCode == 6 && update.ErrorOrStatusCode == 10)
        {
            AddPresentationEvent(new(0, "", Net7ChatPresentationKind.PrivateChannelJoinEntry));
            AddPresentationEvent(new(0, "cost.wav", Net7ChatPresentationKind.InterfaceSound));
        }
        // 0073D170 -> 00567DF0 modes 12/13 -> 00566AF0 states 6/7.
        // Required-password is silent; incorrect-password plays cost.wav.
        if (update.RequestCode == 6 && update.ErrorOrStatusCode is 12 or 13)
        {
            AddPresentationEvent(new(0, "", update.ErrorOrStatusCode == 12
                ? Net7ChatPresentationKind.PrivateChannelPasswordRequired
                : Net7ChatPresentationKind.PrivateChannelPasswordIncorrect));
            if (update.ErrorOrStatusCode == 13)
                AddPresentationEvent(new(0, "cost.wav", Net7ChatPresentationKind.InterfaceSound));
        }
        string? text = Net7ChatResponseText.Format(update);
        if (!string.IsNullOrEmpty(text))
        {
            ApplyBaseMessage(new(19, text));
            AddPresentationEvent(new(19, "Text_Error", Net7ChatPresentationKind.Error));
        }
    }

    public void ApplyGuildMessage(Net7GuildMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Net7GuildMessagePresentation? presentation = Net7GuildMessageCatalog.Resolve(message.MessageType, message.OtherName, message.GuildName);
        if (presentation is null) return; // safety rule: 0073EB91; preserve prior state without reporting an issue.
        GuildPresentation = presentation;
        if (GuildWindowActive)
        {
            switch (presentation.Kind)
            {
                case Net7GuildMessagePresentationKind.GuildRoster:
                    GuildRosterLines = presentation.Lines;
                    GuildRosterEntries = presentation.Lines
                        .Select(Net7GuildRosterEntry.Parse)
                        .OrderBy(x => x.IsOffline)
                        .ThenBy(x => x.Identity, StringComparer.Ordinal)
                        .ToArray();
                    return;
                case Net7GuildMessagePresentationKind.SecondaryList:
                    // 0074DFF0 -> 0081C1F0/0081EEA0 opens/replaces; while
                    // visible, 0081C060/0081EC90 appends to the same display.
                    GuildSecondaryListLines = GuildSecondarySubview == Net7GuildSecondarySubview.First
                        ? presentation.Lines : GuildSecondaryListLines.Concat(presentation.Lines).ToArray();
                    if (GuildSecondarySubview == Net7GuildSecondarySubview.First)
                        GuildSecondaryFirstLines = presentation.Lines;
                    else
                        GuildSecondarySecondLines = presentation.Lines;
                    GuildSecondarySubview = Net7GuildSecondarySubview.Second;
                    GuildDisplayRevision++;
                    return;
                case Net7GuildMessagePresentationKind.GuildWindowSuppressed:
                    return;
            }
        }
        AddHistory(new Net7MessageState(8, presentation.FormattedText));
        // 0073E9E0 plays the catalog cue after presenting an ordinary guild message.
        if (!string.IsNullOrEmpty(presentation.Sound) && presentation.Sound != "<null>")
            AddPresentationEvent(new(8, presentation.Sound, Net7ChatPresentationKind.GuildSound));
    }

    public void AddHistory(Net7MessageState message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ChatRevision++;
        if (_messages.Count == ChatHistoryCapacity)
            _messages.RemoveAt(0);
        _messages.Add(message with { Sequence = ++_messageSequence });
    }
    public void ApplyScreenLine(EnbNext.Net7.Presentation.Net7MessageLineUpdate line)
        => AddPresentationEvent(new(0, line.Text, Net7ChatPresentationKind.ScreenLine) { ScreenLine = line });

    public void ApplyInterfaceSound(string name)
        => AddPresentationEvent(new(0, name, Net7ChatPresentationKind.InterfaceSound));

    public void ApplyNamedScreenLine(string name)
        => AddPresentationEvent(new(0, name, Net7ChatPresentationKind.NamedScreenLine));

    public void ClearScreenLines()
        => AddPresentationEvent(new(0, "", Net7ChatPresentationKind.ClearScreenLines));

    private void AddPresentationEvent(Net7ChatPresentationEvent value)
    {
        ChatRevision++;
        if (_presentationEvents.Count == ChatHistoryCapacity) _presentationEvents.RemoveAt(0);
        _presentationEvents.Add(value with { Sequence = ++_eventSequence });
    }
    private void PresentSpeaker(string identity, byte type)
    {
        if (identity.Length == 0) return;
        AddPresentationEvent(new(type, identity, Net7ChatPresentationKind.SpeakerColor));
        SpeakerPresented?.Invoke(identity, type);
    }

    public void PromoteSecureTarget(string identity)
    {
        ChatRevision++;
        // 005658B0 byte-compares the name with the three slots (empty slots
        // compare as ""). A match only selects that slot; otherwise the slots
        // shift down and the name enters slot 0. No name is rejected.
        if (_secureTargets.Contains(identity,StringComparer.Ordinal) ||
            identity.Length == 0 && _secureTargets.Count < SecureTargetCapacity) return;
        _secureTargets.Insert(0, identity);
        if (_secureTargets.Count > SecureTargetCapacity)
            _secureTargets.RemoveAt(_secureTargets.Count - 1);
    }

    public void SelectSecureRecipient(string identity)
    {
        PromoteSecureTarget(identity);
        AddPresentationEvent(new(0,identity,Net7ChatPresentationKind.SelectSecureRecipient));
    }

    public Net7PrivateFriendActionPlan PlanPrivateChatWithFriend(string identity)
    {
        if (!_friends.TryGetValue(identity, out Net7FriendEntry? friend))
            return new Net7PrivateFriendActionPlan(false, null, false, null);
        if (friend.IsOffline)
            return new Net7PrivateFriendActionPlan(false, friend.Identity, false, "Friend not online.");
        return new Net7PrivateFriendActionPlan(true, friend.Identity, true, null);
    }

    public bool CanStartPrivateChatWithFriend(string identity)
        => PlanPrivateChatWithFriend(identity).ShouldOpenPrivateUi;

    public uint SelectPresenceRequestCode(bool restricted)
    {
        RestrictedStatus = restricted;
        ChatRevision++;
        return restricted ? 28u : 29u;
    }


    public void ResetForHandoff()
    {
        FriendsWindowActive = false;
        IgnoreWindowActive = ChannelMembersWindowActive = false; _membersWindowChannel = "";
        _avatarEmotes.Clear(); _avatarSpeech.Clear(); ChatRevision++;
        GroupInvite = null; _queuedGroupInvites.Clear();
        GroupFormationVisualEnabled = false;
        ResetGroupAttention();
        _highlightedGroupAvatarIds.Clear();
        GroupFinderCandidates = Array.Empty<Net7GroupFinderCandidate>();
        SetGroupFinderWindow(false);
        GuildPresentation = null;
        GuildRosterLines = Array.Empty<string>();
        GuildRosterEntries = Array.Empty<Net7GuildRosterEntry>();
        GuildSecondaryListLines = Array.Empty<string>();
        GuildSecondaryFirstLines = Array.Empty<string>();
        GuildSecondarySecondLines = Array.Empty<string>();
    }

    public void ResetForCharacterSelection()
    {
        ChatRevision++;
        StreamId = Guid.NewGuid();
        _messageSequence = 0;
        _eventSequence = 0;
        _messages.Clear();
        _presentationEvents.Clear();
        _avatarEmotes.Clear(); _avatarEmoteSequence = 0;
        _avatarSpeech.Clear(); _avatarSpeechSequence = 0;
        _groupSymbols.Clear(); _groupSymbolSequence = 0;
        FriendsRevision++;
        _friends.Clear();
        _ignored.Clear(); _ignoredRows.Clear(); IgnoredRevision++;
        _secureTargets.Clear();
        _recentPrivatePeers.Clear();
        _channelMembers.Clear();
        _channelMemberRevisions.Clear();
        ActiveChannels = Array.Empty<string>();
        CurrentChannels = Array.Empty<string>();
        PrivateChannel = ""; PrivateChannelOwner = false;
        ReplyIdentity = null;
        LastChatError = null;
        // RestrictedStatus is a persisted client preference, not character or
        // server-authored state. Preserve it across character-selection resets.
        GuildWindowActive = false;
        GuildSecondarySubview = Net7GuildSecondarySubview.First;
        ResetForHandoff();
    }


    internal static string DecodeChatErrorReason(uint code)
        => code switch
        {
            0 => "ok",
            1 => "unknown_error",
            2 => "invalid channel",
            3 => "invalid parameter",
            4 => "invalid person",
            5 => "duplicate name",
            6 => "not a member",
            7 => "already",
            8 => "is ignored",
            9 => "is gagged",
            10 => "is banned",
            11 => "is system channel",
            12 => "need password",
            13 => "wrong password",
            14 => "no permission",
            15 => "bad name",
            16 => "no memory",
            17 => "yourself",
            18 => "reached max limit",
            // 00830AA0 returns this text for every unsigned code above 18.
            _ => "bad chat error",
        };

    private void SetReply(string identity)
    {
        // 00565D80 stores the reply name, then byte-compares it with the five
        // recent slots (empty slots compare as ""). A match only selects that
        // slot; otherwise the slots shift down and the name enters slot 0.
        ReplyIdentity = identity;
        if (_recentPrivatePeers.Contains(identity, StringComparer.Ordinal) ||
            identity.Length == 0 && _recentPrivatePeers.Count < RecentPrivatePeerCapacity)
            return;
        _recentPrivatePeers.Insert(0, identity);
        if (_recentPrivatePeers.Count > RecentPrivatePeerCapacity)
            _recentPrivatePeers.RemoveAt(_recentPrivatePeers.Count - 1);
    }

    private void AddSystemContext(string text)
        => ApplyBaseMessage(new Net7MessageState(0, text));

    private static string StripChannelSuffix(string value)
    {
        int tick = value.IndexOf('`');
        return tick >= 0 ? value[..tick] : value;
    }

    /// <summary>Next: the channel name of a public or private channel line (types 9-15), for chat filters; null for sector, group and guild.</summary>
    private static string? NamedChannel(byte type, string channel) => type is >= 9 and <= 15 ? channel : null;

    public static byte ChannelMessageType(string channel)
    {
        if (channel.StartsWith("Sector", StringComparison.OrdinalIgnoreCase)) return 2;
        if (channel.StartsWith("Group", StringComparison.OrdinalIgnoreCase)) return 4;
        if (channel.StartsWith("Guild", StringComparison.OrdinalIgnoreCase)) return 8;
        // 0073B150 calls the C integer conversion at 00A27762: positive
        // numeric names use the private-channel color, not arbitrary names.
        var number = channel.AsSpan().TrimStart();
        bool negative = number.Length > 0 && number[0] == '-';
        if (number.Length > 0 && number[0] is '+' or '-') number = number[1..];
        int value = 0;
        foreach (char digit in number) { if (digit is < '0' or > '9') break; value = unchecked(value * 10 + digit - '0'); }
        if (!negative && value > 0) return 13;
        return channel.ToLowerInvariant() switch
        {
            "general" => 9, "out of context" or "ooc" => 10, "new players" => 11, "market" => 12,
            "terran" or "jenquai" or "progen" => 14,
            "explorers" or "tradesmen" or "warriors" or "sentinels" or "defenders" or "enforcers" => 15,
            _ => 15,
        };
    }

    private static void RemoveOrdinal(List<string> values, string identity)
    {
        int index = values.FindIndex(x => string.Equals(x, identity, StringComparison.Ordinal));
        if (index >= 0)
            values.RemoveAt(index);
    }
}
