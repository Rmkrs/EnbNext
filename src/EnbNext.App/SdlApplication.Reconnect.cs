using EnbNext.Client;
using EnbNext.Client.Models;
using EnbNext.Net7;
namespace EnbNext.App;
/// <summary>
/// Next: recovery from a session the server stopped serving (a dock, gate or launch whose transfer never arrives).
/// /reconnect drops the session, logs the same account in again and enters the same character, so the player lands
/// wherever the server now has them. When such a transfer is 20 seconds late the chat says so and offers it.
/// </summary>
internal sealed partial class SdlApplication
{
    private const long StuckTransferMilliseconds = 20000;
    private bool _reconnecting;
    private long _stuckNoticeStart = long.MinValue, _stuckCheck;

    private bool TryReconnectCommand(string text)
    {
        if (!string.Equals(text.Trim(), "/reconnect", StringComparison.OrdinalIgnoreCase)) return false;
        dispatch.Observe(ReconnectAsync());
        return true;
    }

    /// <summary>Once per stuck transfer, a local chat line names it and offers /reconnect.</summary>
    private void TickStuckAnswer()
    {
        long now = Environment.TickCount64;
        if (now - _stuckCheck < 1000 || _client is not { } client) return;
        _stuckCheck = now;
        if (client.Connection is not { } s || s.Awaited is Net7AwaitedAnswer.None or Net7AwaitedAnswer.Conversation) return;
        if (s.AwaitedMilliseconds < StuckTransferMilliseconds) return;
        long start = now - s.AwaitedMilliseconds;
        if (Math.Abs(start - _stuckNoticeStart) < 2000) return;
        _stuckNoticeStart = start;
        string what = s.Awaited switch
        {
            Net7AwaitedAnswer.Dock => "docked you",
            Net7AwaitedAnswer.Landing => "landed you",
            Net7AwaitedAnswer.Gate => "taken you through the gate",
            _ => "launched you from the station",
        };
        string cause = s.ServerAnswersWhileAwaiting switch
        {
            true => " It still answers otherwise, so the problem is on the server's side.",
            false => " It does not answer at all: your internet connection or the server may be down.",
            _ => "",
        };
        AppendDiagnostic($"STUCK TRANSFER: {s.Awaited} unanswered for {s.AwaitedMilliseconds} ms; server answers: {s.ServerAnswersWhileAwaiting?.ToString() ?? "unknown"}");
        if (client.SelectedFixedSessionId is uint character)
            client.ReportLocalChatMessage(character, 19, $"The server has not {what} after {s.AwaitedMilliseconds / 1000} seconds.{cause} Type /reconnect to log in again.");
    }

    /// <summary>Drops the current session and logs the same account and character in again.</summary>
    private async Task ReconnectAsync()
    {
        if (_reconnecting || _busy || _client is not { } old || _selectedCharacter is not { } character) return;
        if (!old.TryGetLoginCredentials(out string username, out string password))
        {
            if (old.SelectedFixedSessionId is uint id) old.ReportLocalChatMessage(id, 19, "Reconnect is not available for this session.");
            return;
        }
        string host = old.ServerHost;
        _reconnecting = true;
        AppendDiagnostic($"RECONNECT: dropping the session and logging {character.FullName} in again");
        try
        {
            await DisposeClientAsync();
            if (_frontendClosed) return;
            ShowLoginScreen();
            SetBusy(true);
            SetStatus($"Reconnecting {character.FullName}...", isError: false);
            _client = CreateClient(host);
            IReadOnlyList<CharacterSummary> characters = await _client.LoginAsync(username, password);
            if (_frontendClosed) return;
            _selectedCharacter = StartupLogin.ForMember(username, character.FullName, host).Resolve(characters);
            SetBusy(false);
            if (_selectedCharacter is null)
            {
                await ShowCharacterScreen(characters);
                SetStatus($"{character.FullName} was not found on the account; choose a character.", isError: true);
                return;
            }
            await EnterSelectedCharacterAsync();
        }
        catch (Exception ex)
        {
            AppendDiagnostic("RECONNECT FAILED: " + ex);
            await DisposeClientAsync();
            if (_frontendClosed) return;
            ShowLoginScreen();
            await ShowLoginFailureAsync(ex, null);
        }
        finally
        {
            _reconnecting = false;
            SetBusy(false);
        }
    }
}
