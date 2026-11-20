using EnbNext.Presentation;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Social;

namespace EnbNext.Client;

public sealed partial class EnbClient
{
    public Task<bool> GuildAsync(EnbGuildRequest request, CancellationToken cancellationToken = default)
    {
        if (_net7 is not {} session || State != ClientLifecycleState.InWorld) return Task.FromResult(false);
        return session.ReadPresentationFrame((world, time) =>
        {
            if (!ReferenceEquals(session, _net7) || request.Character != session.FixedSessionId) return Task.FromResult(false);
            if (request.Action == EnbGuildAction.Close)
            { session.SetGuildWindow(false); return Task.FromResult(true); }
            if (LatestPresentationSnapshot is not { IsTransitioning: false } snapshot ||
                snapshot.Objects.FirstOrDefault(o => o.IsLocalShip)?.RenderIdentity != request.ShipOwner) return Task.FromResult(false);
            if (request.Action is EnbGuildAction.Accept or EnbGuildAction.Reject)
            {
                if (request.Prompt is null || !ReferenceEquals(request.Prompt, session.Gameplay.GuildPrompt)) return Task.FromResult(false);
                bool requiresReply = Net7SocialGuildPolicy.PlanPromptReply(request.Character, request.Prompt, request.Action == EnbGuildAction.Accept).ShouldSend;
                var reply = session.ReplyPendingGuildPromptAsync(request.Action == EnbGuildAction.Accept, cancellationToken);
                OnNet7PresentationStateChanged(time);
                return requiresReply ? reply : Task.FromResult(true);
            }
            int rank = session.PlayerSemantics.GetGuildMembership().GuildRank;
            var guild = EnbSceneGuildSnapshot.From(session.Gameplay);
            if (!guild.Allows(request.Action, rank, request.Member)) return Task.FromResult(false);
            if (request.Action == EnbGuildAction.Open)
            {
                session.SetGuildWindow(true);
                return rank == -1 ? Task.FromResult(true) : OpenGuild();
            }
            if (!session.Gameplay.Social.GuildWindowActive) return Task.FromResult(false);
            if (request.Action == EnbGuildAction.CloseDisplay)
            { session.SetGuildSubview(Net7GuildSecondarySubview.First); return Task.FromResult(true); }
            var command = request.Action switch
            {
                EnbGuildAction.Recruit => Net7ExactServerChatCommand.GuildRecruit,
                EnbGuildAction.Remove => Net7ExactServerChatCommand.GuildRemove,
                EnbGuildAction.Promote => Net7ExactServerChatCommand.GuildPromote,
                EnbGuildAction.Demote => Net7ExactServerChatCommand.GuildDemote,
                EnbGuildAction.Disband => Net7ExactServerChatCommand.GuildDisband,
                EnbGuildAction.Tag => Net7ExactServerChatCommand.GuildTag,
                EnbGuildAction.Message => Net7ExactServerChatCommand.GuildMessage,
                EnbGuildAction.Public => Net7ExactServerChatCommand.GuildPublicStatsOn,
                EnbGuildAction.Private => Net7ExactServerChatCommand.GuildPublicStatsOff,
                EnbGuildAction.Leave => Net7ExactServerChatCommand.GuildLeave,
                EnbGuildAction.Contributions => Net7ExactServerChatCommand.GuildContributions,
                EnbGuildAction.Stats => Net7ExactServerChatCommand.GuildStats,
                EnbGuildAction.Create => Net7ExactServerChatCommand.GuildCreate,
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            };
            string? argument = request.Action is EnbGuildAction.Remove or EnbGuildAction.Promote or EnbGuildAction.Demote or EnbGuildAction.Tag or EnbGuildAction.Contributions ? request.Member : request.Text;
            if (argument?.Any(c => c < 32 || c > 255) == true || request.Text?.Any(c => c < 32 || c > 255) == true) return Task.FromResult(false);
            if (request.Action is EnbGuildAction.Stats or EnbGuildAction.Contributions)
                session.SetGuildSubview(Net7GuildSecondarySubview.First);
            try { return session.SendGuildCommandAsync(command, argument, request.Action == EnbGuildAction.Tag ? request.Text : null, cancellationToken); }
            catch (ArgumentException) { return Task.FromResult(false); }

            async Task<bool> OpenGuild()
            {
                if (!await session.RequestGuildRankNamesAsync(cancellationToken).ConfigureAwait(false)) return false;
                return await session.SendGuildCommandAsync(Net7ExactServerChatCommand.GuildListAll, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        });
    }
}
