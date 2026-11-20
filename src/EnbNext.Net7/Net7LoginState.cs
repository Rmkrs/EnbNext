namespace EnbNext.Net7;

/// <summary>Fail-closed direct-Net7 transaction state through sector entry.</summary>
public enum Net7LoginState
{
    Created,
    Bootstrapping,
    AccountConnecting,
    Authenticating,
    LoadingRoster,
    CharacterSelection,
    SelectingCharacter,
    CharacterSelected,
    MasterHandoff,
    SectorJoining,
    SectorReady,
    SectorTransition,
    Faulted,
    Disposed,
}
