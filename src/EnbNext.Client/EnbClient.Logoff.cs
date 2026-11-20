using EnbNext.Net7;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Client;

public sealed partial class EnbClient
{
    private (string Username, string Password)? _loginCredentials;

    /// <summary>The server this client logs in to.</summary>
    public string ServerHost => _options.ServerHost;

    /// <summary>Next: the credentials of the current login, for a reconnect that logs the same account in again.</summary>
    public bool TryGetLoginCredentials(out string username, out string password)
    {
        (username, password) = _loginCredentials ?? ("", "");
        return _loginCredentials is not null;
    }

    public async Task<IReadOnlyList<Models.CharacterSummary>> RefreshCharacterSelectionAsync(CancellationToken cancellationToken = default)
    {
        if (State != ClientLifecycleState.CharacterSelection || _net7 is not {} session || _loginCredentials is not {} credentials)
            throw new InvalidOperationException("Character selection requires the current authenticated account.");
        Characters = Array.Empty<Models.CharacterSummary>();
        try
        {
            var roster = await session.RefreshCharacterRosterAsync(credentials.Username, credentials.Password, cancellationToken).ConfigureAwait(false);
            Characters = MapCharacters(roster);
            SetState(ClientLifecycleState.CharacterSelection);
            return Characters;
        }
        catch { SetState(ClientLifecycleState.Faulted); throw; }
    }

    public event Action<Net7LogoffDestination>? LogoffCompleted;

    public Task<bool> RequestLogoffAsync(uint character, ulong handoffVersion, Net7LogoffDestination destination,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(destination)) throw new ArgumentOutOfRangeException(nameof(destination));
        if (character == 0 || State != ClientLifecycleState.InWorld || _net7 is not { } session)
            return Task.FromResult(false);
        return session.ReadPresentationFrame((world, _) =>
        {
            if (!ReferenceEquals(_net7, session) || State != ClientLifecycleState.InWorld ||
                session.FixedSessionId != character || world.IsTransitioning ||
                world.PresentationHandoffVersion != handoffVersion || session.PlayerId is null) return Task.FromResult(false);
            // 0074BB50: a choice made while an earlier logoff is still unconfirmed first says so on the system channel
            // (0x13, AddChatMessage 004095A7), then sends the request again; the latest destination is the one kept.
            if (session.PendingLogoffDestination is not null)
                session.ReportLocalChatMessage(character, 0x13, destination switch
                {
                    Net7LogoffDestination.Desktop => "Logging off to desktop.",
                    Net7LogoffDestination.LoginScreen => "Logging off to login screen.",
                    _ => "Logging off to character selection.",
                });
            return session.RequestLogoffAsync(destination, cancellationToken);
        });
    }

    private void OnNet7LogoffConfirmed(Net7Session session, Net7LogoffDestination? destination)
    {
        if (!ReferenceEquals(_net7, session)) return;
        // Server confirmation has already retired the lower session identity.
        // The frontend refreshes authentication and the roster before selection.
        _presentationSnapshots.Clear();
        Route = null;
        SelectedCharacter = null;
        SelectedFixedSessionId = null;
        _automaticSectorTransitionActive = false;
        SetState(ClientLifecycleState.CharacterSelection);
        if (destination is { } selected) LogoffCompleted?.Invoke(selected);
    }
}
