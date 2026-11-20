using EnbNext.Net7.Presentation;

namespace EnbNext.Client;

/// <summary>
/// Next route wormholes: auto pilot opens a wormhole with the ordinary skill ability command (as its shortcut does), and
/// every client of the player's that expects it answers the server's wormhole question with Yes itself, without
/// showing it. Only a question about a wormhole, arriving while one is expected, is answered.
/// </summary>
public sealed partial class EnbClient
{
    private long _wormholeExpectedUntil;
    private Net7ConfirmedActionPending? _wormholeAnswered;
    internal static readonly long WormholeExpectation = 60000;

    /// <summary>Opens the wormhole of an ability (Create Wormhole or Extended Wormhole) in space.</summary>
    public Task<bool> OpenWormholeAsync(int ability, CancellationToken cancellationToken = default)
    {
        if (ability < 0 || State != ClientLifecycleState.InWorld || _net7 is not { } session ||
            LatestPresentationSnapshot is not { IsTransitioning: false, ActiveStarbaseId: null }) return Task.FromResult(false);
        ExpectWormhole();
        return session.ActivateSkillAsync((uint)ability, cancellationToken);
    }

    /// <summary>A group formation request in space (4 Slot, 5 Block, 6 Pipe, 7 join, 8 leave, 9 break), as the group window sends it.</summary>
    public Task<bool> RequestFormationAsync(uint type, CancellationToken cancellationToken = default)
        => type is >= 4 and <= 9 && State == ClientLifecycleState.InWorld && _net7 is { } session &&
           LatestPresentationSnapshot is { IsTransitioning: false, ActiveStarbaseId: null }
            ? session.SendGroupCtaPanelRequestAsync((EnbNext.Net7.Social.Net7GroupCtaPanelRequestType)type, cancellationToken) : Task.FromResult(false);

    /// <summary>The next wormhole question within a minute is answered with Yes.</summary>
    public void ExpectWormhole() => _wormholeExpectedUntil = Environment.TickCount64 + WormholeExpectation;

    /// <summary>A question this client answers itself (an expected wormhole), so it is not shown.</summary>
    public bool AnswersItself(Net7ConfirmedActionPending question)
        => Environment.TickCount64 < _wormholeExpectedUntil && question.Message.Contains("wormhole", StringComparison.OrdinalIgnoreCase);

    /// <summary>Answers an open expected wormhole question with Yes (once).</summary>
    public void AnswerExpectedWormhole()
    {
        if (State != ClientLifecycleState.InWorld || _net7 is not { } session || session.Gameplay.ConfirmedDialog is not { } question ||
            ReferenceEquals(question, _wormholeAnswered) || !AnswersItself(question)) return;
        _wormholeAnswered = question; _wormholeExpectedUntil = 0;
        Trace("Auto pilot accepted the wormhole");
        session.RespondConfirmedActionAsync(0, question.Id, true).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }
}
