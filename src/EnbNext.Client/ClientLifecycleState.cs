namespace EnbNext.Client;

public enum ClientLifecycleState
{
    Disconnected,
    ResolvingServer,
    Bootstrapping,
    AccountConnecting,
    Authenticating,
    LoadingCharacters,
    CharacterSelection,
    AvatarLogin,
    CharacterSelected,
    MasterHandoff,
    SectorJoining,
    StartingWorld,
    InWorld,
    Redirecting,
    Disconnecting,
    Faulted,
}
