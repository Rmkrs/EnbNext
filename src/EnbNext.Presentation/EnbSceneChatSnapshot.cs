using System.Collections.Immutable;
using EnbNext.Net7.Social;

namespace EnbNext.Presentation;

public sealed record EnbChatLine(long Sequence, byte Type, string Text, string? Peer, string? Channel = null);
public sealed record EnbSceneChatSnapshot(Guid StreamId, ImmutableArray<EnbChatLine> Lines,
    ImmutableArray<Net7ChatPresentationEvent> Events, ImmutableArray<string> Channels,
    ImmutableArray<Net7FriendEntry> Friends, ImmutableArray<string> Ignored,
    string? ReplyIdentity, ImmutableArray<string> SecureTargets)
{
    public EnbNext.Net7.Presentation.Net7ConfirmedActionPending? IncomingMessage { get; init; }
    /// <summary>The server's yes/no question (CONFIRMED_ACTION_DIALOG, e.g. entering a wormhole) while it is open.</summary>
    public EnbNext.Net7.Presentation.Net7ConfirmedActionPending? ConfirmedDialog { get; init; }
    public ImmutableArray<string> RecentPeers { get; init; } = [];
    public string? LastCreatedChannel { get; init; }
    public long CreatedChannelRevision { get; init; }
    public string PrivateChannel { get; init; } = "";
    public bool PrivateChannelOwner { get; init; }
    public ImmutableDictionary<string, ImmutableArray<string>> ChannelMembers { get; init; } = ImmutableDictionary<string, ImmutableArray<string>>.Empty;
    public ImmutableDictionary<string, long> ChannelMemberRevisions { get; init; } = ImmutableDictionary<string, long>.Empty;
    public long IgnoredRevision { get; init; }
    public long FriendsRevision { get; init; }
    public bool RestrictedStatus { get; init; }
    public ImmutableArray<Net7AvatarEmoteEvent> AvatarEmotes { get; init; } = [];
    public ImmutableArray<Net7AvatarSpeechEvent> AvatarSpeech { get; init; } = [];
    private sealed class Cache { public long Revision = -1; public EnbSceneChatSnapshot? Value; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Net7SocialState, Cache> Caches = new();
    public static EnbSceneChatSnapshot From(Net7SocialState state, EnbNext.Net7.Presentation.Net7ConfirmedActionPending? incomingMessage = null,
        EnbNext.Net7.Presentation.Net7ConfirmedActionPending? confirmedDialog = null)
    {
        var cache = Caches.GetOrCreateValue(state);
        if (cache.Revision == state.ChatRevision && cache.Value is { } existing && ReferenceEquals(existing.IncomingMessage, incomingMessage) &&
            ReferenceEquals(existing.ConfirmedDialog, confirmedDialog)) return existing;
        cache.Revision = state.ChatRevision;
        return cache.Value = new(state.StreamId,
        state.Messages.Select(m => new EnbChatLine(m.Sequence, m.Type, m.Text, m.RoutingIdentity, m.Channel)).ToImmutableArray(),
        state.PresentationEvents.ToImmutableArray(), state.CurrentChannels.ToImmutableArray(),
        state.Friends.Values.ToImmutableArray(), state.IgnoredRows.OrderBy(name => name, StringComparer.Ordinal).ToImmutableArray(), state.ReplyIdentity,
        state.SecureTargets.ToImmutableArray()) { IncomingMessage = incomingMessage, ConfirmedDialog = confirmedDialog, IgnoredRevision = state.IgnoredRevision, FriendsRevision = state.FriendsRevision, RestrictedStatus = state.RestrictedStatus, RecentPeers = state.RecentPrivatePeers.ToImmutableArray(), AvatarEmotes = state.AvatarEmotes.ToImmutableArray(),
            AvatarSpeech = state.AvatarSpeech.ToImmutableArray(),
            LastCreatedChannel = state.LastCreatedChannel, CreatedChannelRevision = state.CreatedChannelRevision,
            PrivateChannel = state.PrivateChannel, PrivateChannelOwner = state.PrivateChannelOwner,
            ChannelMemberRevisions = state.ChannelMemberRevisions.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            ChannelMembers = state.ChannelMembers.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableArray(), StringComparer.OrdinalIgnoreCase) };
    }
}
