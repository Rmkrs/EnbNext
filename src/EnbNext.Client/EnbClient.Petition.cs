namespace EnbNext.Client;

public sealed partial class EnbClient
{
    public Task<bool> SubmitPetitionAsync(uint character, ulong handoff, uint category, string subject, string details, string players)
    {
        if (character == 0 || _net7 is not { } session || category is < 1 or > 7) return Task.FromResult(false);
        return session.ReadPresentationFrame((_, _) =>
        {
            if (!ReferenceEquals(_net7,session) || State != ClientLifecycleState.InWorld || session.FixedSessionId != character ||
                session.World.IsTransitioning || session.World.PresentationHandoffVersion != handoff) return Task.FromResult(false);
            // 005D74C4 / 005D66BC read SClient+1130: current target, NOT local player.
            // 00726A00 owns that field; the second numeric field is the authored petition category.
            uint target = (session.Gameplay.HasLocalTargetSelection ? session.Gameplay.LocalSelectedTargetId : session.World.CurrentTargetId) ?? 0;
            return session.SendPetitionStuckAsync(target, category, subject, details, players);
        });
    }
}
