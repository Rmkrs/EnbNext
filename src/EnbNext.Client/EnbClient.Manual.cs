using EnbNext.Net7;

namespace EnbNext.Client;

public sealed partial class EnbClient
{
    /// <summary>Retail action 6 uses the same BrowserLink parameter map as news.
    /// A popup owns a character and handoff, but is not a station terminal.</summary>
    public string? GetManualBrowserQuery(uint character, ulong handoffVersion)
    {
        if (character == 0 || _net7 is not { } session) return null;
        return session.ReadPresentationFrame((_, _) =>
            ReferenceEquals(_net7, session) && State == ClientLifecycleState.InWorld &&
            session.FixedSessionId == character && !session.World.IsTransitioning &&
            session.World.PresentationHandoffVersion == handoffVersion &&
            session.CharacterSelection is { } selected
                ? Net7StationBrowser.BuildQuery(selected.SectorJoinData) : null);
    }
}
