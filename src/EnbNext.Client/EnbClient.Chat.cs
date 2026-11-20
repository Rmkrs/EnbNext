using EnbNext.Client.Models;
using EnbNext.Net7.Social;

namespace EnbNext.Client;

public sealed partial class EnbClient
{
    public void SetChatColors(uint character,System.Collections.Immutable.ImmutableArray<EnbNext.Assets.EnbRgb> colors)
        => _net7?.SetChatColors(character,System.Collections.Immutable.ImmutableArray.CreateRange(colors.Select(c=>new System.Numerics.Vector3(c.R,c.G,c.B))));
    public EnbNext.Presentation.EnbSceneSnapshot? SamplePresentationSnapshot()
    {
        // 005A69B0 consumes coalesced mission changes once per UI update,
        // before the same update advances its screen-message queue.
        if (State == ClientLifecycleState.InWorld) _net7?.AdvanceLocalMissionPresentation();
        return LatestPresentationSnapshot;
    }

    public void SetChatListWindow(Guid stream, bool ignore, bool members, string channel)
    {
        if (State == ClientLifecycleState.InWorld && _net7 is {} session)
            session.SetChatListWindow(stream, ignore, members, channel);
    }
    public void ReportCameraMessage(uint character, string message)
    {
        if (State == ClientLifecycleState.InWorld && _net7 is {} session)
            session.ReportCameraMessage(character, message);
    }
    public void ReportScreenshotMessage(uint character, bool complete)
    {
        if (State == ClientLifecycleState.InWorld && _net7 is { } session)
            session.ReportScreenshotMessage(character, complete);
    }
    public void ReportLocalInterfaceMessage(uint character, string message)
    {
        if (State == ClientLifecycleState.InWorld && _net7 is {} session)
            session.ReportLocalInterfaceMessage(character, message);
    }
    public void ReportLocalChatMessage(uint character, byte type, string message, bool suppressPrefix = false)
    {
        if (State == ClientLifecycleState.InWorld && _net7 is { } session)
            session.ReportLocalChatMessage(character, type, message, suppressPrefix);
    }
    public Task<bool> SetMonitoredChatChannelAsync(string channel, bool enabled, CancellationToken cancellationToken = default)
        => State == ClientLifecycleState.InWorld && _net7 is { } session
            ? session.SendChatControlRequestAsync(enabled ? Net7ClientChatRequestKind.EnterChannel : Net7ClientChatRequestKind.ExitChannel,
                channel: channel, value: " ", cancellationToken: cancellationToken)
            : Task.FromResult(false);
    public Task<bool> SendPrivateChannelControlAsync(Net7ClientChatRequestKind kind, string? channel, string? value = null,
        CancellationToken cancellationToken = default)
        => State == ClientLifecycleState.InWorld && _net7 is { } session
            ? session.SendChatControlRequestAsync(kind, channel: channel, value: value, cancellationToken: cancellationToken)
            : Task.FromResult(false);
    public Task<bool> SendIgnorePlayerControlAsync(Net7ClientChatRequestKind kind, string player,
        CancellationToken cancellationToken = default)
        => State == ClientLifecycleState.InWorld && _net7 is { } session
            ? session.SendChatControlRequestAsync(kind, player: player, cancellationToken: cancellationToken)
            : Task.FromResult(false);
    public Task<bool> SendPrivateMemberControlAsync(Net7ClientChatRequestKind kind, string player, string channel,
        CancellationToken cancellationToken = default)
        => State == ClientLifecycleState.InWorld && _net7 is { } session
            ? session.SendChatControlRequestAsync(kind, player: player, channel: channel, cancellationToken: cancellationToken)
            : Task.FromResult(false);
    public void ReportQuickMessageSaved(uint character, int slot)
    {
        if (State == ClientLifecycleState.InWorld && _net7 is { } session)
            session.ReportQuickMessageSaved(character, slot);
    }
    public Task<bool> AcceptIncomingMessageAsync(uint character, uint id, CancellationToken cancellationToken = default)
        => State == ClientLifecycleState.InWorld && _net7 is {} session && session.FixedSessionId == character
            ? session.RespondConfirmedActionAsync(1, id, true, cancellationToken) : Task.FromResult(false);

    /// <summary>Answers the server's open yes/no question (type-0 offer) as its Yes or No would.</summary>
    public Task<bool> AnswerConfirmedDialogAsync(uint character, uint id, bool accepted, CancellationToken cancellationToken = default)
        => State == ClientLifecycleState.InWorld && _net7 is {} session && session.FixedSessionId == character
            ? session.RespondConfirmedActionAsync(0, id, accepted, cancellationToken) : Task.FromResult(false);

    public Task<bool> SubmitChatAsync(Net7ChatInputRequest request, CancellationToken cancellationToken = default)
        => State == ClientLifecycleState.InWorld && _net7 is { } session
            ? session.SubmitChatAsync(request, cancellationToken) : Task.FromResult(false);
}
