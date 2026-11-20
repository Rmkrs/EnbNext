using EnbNext.Net7.Social;

namespace EnbNext.Net7;

public sealed partial class Net7Session
{
    public void SetGuildWindow(bool active) => MutateWorldAndPublish(() => Gameplay.Social.SetGuildWindow(active));
    public void SetGuildSubview(Net7GuildSecondarySubview view) => MutateWorldAndPublish(() => Gameplay.Social.GuildSecondarySubview = view);
    public Task<bool> SendGuildCommandAsync(Net7ExactServerChatCommand command, string? argument = null,
        string? second = null, CancellationToken cancellationToken = default)
    {
        // 0081BF40 / 0081CB90 use SClient+112C (0037-assigned identity), not Start.
        var plan = Net7SocialChatPolicy.PlanExactServerCommand(new(RequireLocalAvatarObjectId(), Net7SelectedChatChannel.Local,
            PlayerSemantics.GetGuildMembership().IsInGuild, false), command, argument, second);
        return TrySendGameplayAsync(plan.Opcode, plan.Payload, cancellationToken);
    }
}
