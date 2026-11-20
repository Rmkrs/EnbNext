using EnbNext.Presentation;
using EnbNext.Net7.Social;

namespace EnbNext.Client;

public sealed partial class EnbClient
{
    public Task<bool> FriendsAsync(EnbFriendsRequest request, CancellationToken cancellationToken = default)
    {
        if (_net7 is not {} session || State != ClientLifecycleState.InWorld) return Task.FromResult(false);
        return session.ReadPresentationFrame((world, time) =>
        {
            if (!ReferenceEquals(session, _net7) || request.Character != session.FixedSessionId) return Task.FromResult(false);
            if (request.Action == EnbFriendsAction.Close)
            { session.SetFriendsWindow(false); return Task.FromResult(true); }
            var snapshot = LatestPresentationSnapshot;
            if (snapshot is not { IsTransitioning: false } || snapshot.Objects.FirstOrDefault(o => o.IsLocalShip)?.RenderIdentity != request.ShipOwner)
                return Task.FromResult(false);
            if (request.Action == EnbFriendsAction.Open)
            {
                session.SetFriendsWindow(true);
                return session.SendChatControlRequestAsync(Net7ClientChatRequestKind.ListFriends, cancellationToken: cancellationToken);
            }
            if (!session.Gameplay.Social.FriendsWindowActive) return Task.FromResult(false);
            if (request.Action is EnbFriendsAction.Restricted or EnbFriendsAction.Unrestricted)
            {
                bool restricted = request.Action == EnbFriendsAction.Restricted;
                if (session.Gameplay.Social.RestrictedStatus == restricted) return Task.FromResult(true);
                RetailAssets?.SavePresenceVisibility(request.Character, restricted);
                return session.SetPresenceVisibilityAsync(restricted, cancellationToken);
            }
            if (request.Action == EnbFriendsAction.AddGroup)
            {
                var group = session.PlayerSemantics.GetGroupState();
                return group.IsInGroup ? AddGroup(group.Members.Where(m => !string.IsNullOrEmpty(m.Name)).Select(m => m.Name).ToArray()) : Task.FromResult(false);
            }
            string? name = request.Identity;
            if (request.Action == EnbFriendsAction.AddTarget)
            {
                var target = snapshot.Objects.FirstOrDefault(o => o.IsCurrentTarget && o.ObjectType == 1);
                if (snapshot.ActiveStarbaseId is not null || target is null || EnbRetailObjectDisplayName.Recipient(target) != name) return Task.FromResult(false);
            }
            if (request.Action == EnbFriendsAction.Remove && (name is null || !session.Gameplay.Social.Friends.ContainsKey(name))) return Task.FromResult(false);
            if (name is null || request.Action!=EnbFriendsAction.AddTarget && string.IsNullOrWhiteSpace(name) || name.Any(c => c < 32 || c > 255) || request.Action == EnbFriendsAction.Add && name.Length > 20) return Task.FromResult(false);
            return session.SendChatControlRequestAsync(request.Action == EnbFriendsAction.Remove ? Net7ClientChatRequestKind.Unfriend : Net7ClientChatRequestKind.Friend,
                player: name, cancellationToken: cancellationToken);

            async Task<bool> AddGroup(string[] names)
            {
                foreach (string identity in names)
                    if (!await session.SendChatControlRequestAsync(Net7ClientChatRequestKind.Friend, player: identity, cancellationToken: cancellationToken).ConfigureAwait(false)) return false;
                return true;
            }
        });
    }
}
