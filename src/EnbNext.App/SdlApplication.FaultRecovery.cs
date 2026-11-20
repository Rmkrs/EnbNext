using System.Runtime.ExceptionServices;

namespace EnbNext.App;

/// <summary>
/// The client stays up. A fault escaping a frame (input, queued async work, ticks, rendering) is logged and only
/// that frame is lost; play continues. Only when every frame keeps failing (the game cannot run) is the session
/// closed and the player returned to the login screen to connect again; on the login screen itself the frontend
/// is rebuilt. Connection loss keeps its own client lifecycle path.
/// </summary>
internal sealed partial class SdlApplication
{
    private ExceptionDispatchInfo? _inspectFailure;
    private int _faultStreak;
    private long _faultStreakStart;
    private bool _recoveringFromFault;
    private const int PersistentFaultFrames = 60;
    private const long PersistentFaultMilliseconds = 3000;

    private void FrameSucceeded() => _faultStreak = 0;

    private void RecoverFromFault(Exception fault)
    {
        if (++_faultStreak == 1) _faultStreakStart = Environment.TickCount64;
        bool inSession = worldScreen is not null || videoScreen is not null || movieScreen is not null || _client is not null;
        try
        {
            if (_faultStreak <= 3) AppendDiagnostic($"CONTAINED FAULT ({(inSession ? "session" : "frontend")}): {fault}");
            else if (_faultStreak == 4) AppendDiagnostic("CONTAINED FAULT: repeating; repeats are not logged until a frame succeeds.");
            _runtimeIssues.Flush();
        }
        catch { } // Logging must never become the next fault.
        // A passing fault costs one frame, nothing more.
        if (_faultStreak < PersistentFaultFrames || Environment.TickCount64 - _faultStreakStart < PersistentFaultMilliseconds) return;
        if (_recoveringFromFault || _frontendClosed) return;
        try { AppendDiagnostic($"CONTAINED FAULT: every frame failed for {_faultStreak} frames; returning to the login screen."); } catch { }
        _faultStreak = 0;
        _recoveringFromFault = true;
        try
        {
            Contain(() => { var video = videoScreen; videoScreen = null; video?.Dispose(); });
            Contain(() => { var movie = movieScreen; movieScreen = null; movie?.Dispose(); });
            Contain(CloseWorldWindow);
            // The login screen itself cannot run: build it again from scratch.
            if (!inSession) Contain(ReleaseFrontend);
            dispatch.Observe(ReturnToLoginAfterFaultAsync(inSession));
        }
        catch (Exception ex) { AppendRecoveryFailure(ex); _recoveringFromFault = false; }
    }

    private async Task ReturnToLoginAfterFaultAsync(bool inSession)
    {
        try
        {
            if (inSession) await DisposeClientAsync();
            if (_frontendClosed || _client is not null) return;
            ShowLoginScreen();
            if (inSession) SetStatus("The game hit an error and returned to the login screen. Details are in the session log.", isError: true);
            SetBusy(false);
        }
        catch (Exception ex) { AppendRecoveryFailure(ex); }
        finally { _recoveringFromFault = false; }
    }

    private void Contain(Action step)
    {
        try { step(); }
        catch (Exception ex) { AppendRecoveryFailure(ex); }
    }

    private void AppendRecoveryFailure(Exception ex)
    {
        try { AppendDiagnostic("FAULT RECOVERY STEP FAILED: " + ex); } catch { }
    }
}
