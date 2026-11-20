using EnbNext.Client.Models;
using EnbNext.Net7.Protocol;

namespace EnbNext.Client;

public sealed partial class EnbClient
{
    public IReadOnlyList<Net7GalaxyRoutingRecord> CharacterCreationRoutes =>
        _net7?.Roster?.RoutingRecords.Where(route => route.EnabledSelectableGate != 0 && route.MasterIpv4Raw != 0)
            .GroupBy(route => route.ShardId).Select(group => group.Last()).ToArray()
        ?? [];

    public async Task<IReadOnlyList<CharacterSummary>> CreateCharacterAsync(Net7AvatarCreateRequest request, CancellationToken cancellationToken = default)
    {
        var session = _net7;
        if (session is null || State != ClientLifecycleState.CharacterSelection)
            throw new InvalidOperationException("Character creation requires the character-selection screen.");
        if (request.TargetSlot is < 0 or >= Net7AvatarRosterCodec.SlotCount ||
            !Characters.Any(c => c.Slot == request.TargetSlot && c.IsEmpty))
            throw new InvalidOperationException("The selected character slot is no longer empty.");
        if (!CharacterCreationRoutes.Any(route => route.ShardId == request.TargetShardId))
            throw new InvalidOperationException("The selected galaxy is no longer available for character creation.");
        var result = await session.CreateCharacterAsync(request, cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(session, _net7) || State != ClientLifecycleState.CharacterSelection)
            throw new OperationCanceledException("The account session changed during character creation.");
        Characters = MapCharacters(result.Roster);
        return Characters;
    }

    public async Task<IReadOnlyList<CharacterSummary>> DeleteCharacterAsync(CharacterSummary character, CancellationToken cancellationToken = default)
    {
        var session = _net7;
        if (session is null || State != ClientLifecycleState.CharacterSelection || character.IsEmpty ||
            !Characters.Any(c => c.Slot == character.Slot && c.AvatarId == character.AvatarId && c.AccountId == character.AccountId))
            throw new InvalidOperationException("The selected character is no longer available for deletion.");
        var result = await session.DeleteCharacterAsync(character.Slot, cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(session, _net7) || State != ClientLifecycleState.CharacterSelection)
            throw new OperationCanceledException("The account session changed during character deletion.");
        Characters = MapCharacters(result.Roster);
        return Characters;
    }
}
