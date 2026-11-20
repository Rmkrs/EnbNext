using EnbNext.Presentation;
using System.Diagnostics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    // Diagnostic input selection only: no cached render output and no skipped callbacks.
    private readonly string? _frozenInputControl = Environment.GetEnvironmentVariable("ENB_EXPERIMENT_FROZEN_INPUT_CONTROL");
    private string _frozenInputToken = "";
    private int _frozenInputMode;
    private long _frozenInputPollAfter, _frozenInputExpires;
    private LiveFrame? _frozenWorld, _frozenScanner;
    private ScannerPresentation? _frozenScannerPresentation;
    private uint _frozenMapperClock;

    private void PollFrozenInputExperiment()
    {
        if (_frozenInputControl is null || _tooltipOnly || _embeddedScanner) return;
        long now = Stopwatch.GetTimestamp();
        if (now < _frozenInputPollAfter) return;
        _frozenInputPollAfter = now + Stopwatch.Frequency / 2;
        string token;
        try { token = File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(_frozenInputControl)).Trim(); }
        catch (IOException) { token = "0"; }
        catch (UnauthorizedAccessException) { token = "0"; }
        // A new phase token explicitly captures new inputs. Expired tokens cannot rearm.
        if (token != _frozenInputToken)
        {
            _frozenInputToken = token;
            _frozenWorld = null; _frozenScanner = null; _frozenScannerPresentation = null;
            _frozenInputMode = int.TryParse(token.Split(':')[0], out int mode) && mode is >= 0 and <= 2 ? mode : 0;
            _frozenInputExpires = now + Stopwatch.Frequency * 90;
        }
        var pending = Volatile.Read(ref _pendingFrame);
        if (now >= _frozenInputExpires || pending is null || pending.Movie is not null ||
            pending.Snapshot.IsTransitioning || Volatile.Read(ref _stationInterior) is not null)
        {
            _frozenInputMode = 0; _frozenWorld = null; _frozenScanner = null;
        }
        if (_frozenInputMode == 0 || _frozenWorld is not null) return;
        _frozenWorld = pending!.ForRenderTimestamp(now) with { ClientTimeAnchorTimestamp = now, Snapshot=EnbSceneCapture.CaptureDiagnostics(pending.Snapshot) };
        _frozenMapperClock = MapperClock();
        _frozenScanner = _scannerRenderer is {} scanner ? Volatile.Read(ref scanner._pendingFrame) : null;
        if(_frozenScanner is {} captured)_frozenScanner=captured with {Snapshot=EnbSceneCapture.CaptureDiagnostics(captured.Snapshot)};
        _frozenScannerPresentation = Volatile.Read(ref _scannerPresentation);
    }

    private LiveFrame? SelectFrozenInputFrame(LiveFrame? live, long timestamp)
        => _frozenInputMode switch
        {
            1 when _frozenWorld is not null => _frozenWorld.ForRenderTimestamp(timestamp),
            2 when _frozenWorld is not null => _frozenWorld,
            _ => live?.ForRenderTimestamp(timestamp)
        };

    private void ObserveFrozenInputBoundary(LiveFrame frame)
    {
        if (_frozenInputControl is null) return;
        EnbWorkReuseDiagnostics.Add("input.mode", _frozenInputMode);
        if (!EnbWorkReuseDiagnostics.Active) return;
        using var inspection = EnbWorkReuseDiagnostics.Inspect();
        EnbWorkReuseDiagnostics.Observe("input.snapshot", 0, EnbWorkReuseDiagnostics.Reference(frame.Snapshot));
        EnbWorkReuseDiagnostics.Observe("input.camera", 0, frame.Camera);
        EnbWorkReuseDiagnostics.Observe("input.clocks", 0, frame.CurrentClientTime, ReadMapperClock());
        EnbWorkReuseDiagnostics.Observe("input.randomStreams", 0,
            EnbRetailParticleRandomStreams.Process.Births.Counter,
            EnbRetailParticleRandomStreams.Process.Properties.WordsConsumed);
        // Completed tasks cannot change status again. New/pending tasks invalidate a
        // stable-readiness claim; do not suppress asset completion or wait on a task.
        void Asset(Task task)
        {
            EnbWorkReuseDiagnostics.Observe("input.assetReadiness", task, task.Status);
            if (!task.IsCompleted) EnbWorkReuseDiagnostics.Add("input.assetsPending");
        }
        foreach (var task in _afterburnAssets.Values) Asset(task);
        foreach (var task in _lineAssets.Values) Asset(task);
        foreach (var task in _effectObjectAssets.Values) Asset(task);
        foreach (var task in _effectObjectInstanceAssets.Values) Asset(task);
        foreach (var task in _effectSwatches.Values) Asset(task);
        foreach (var task in _warpTunnelAssets.Values) Asset(task);
        if (_warpPlumeAsset is {} plume) Asset(plume);
        foreach (var pair in _rslSoundCompletions)
            EnbWorkReuseDiagnostics.Observe("input.soundCompletion", pair.Key, pair.Value);
        EnbWorkReuseDiagnostics.Observe("input.assetOwner", 0, _assetGeneration,
            EnbWorkReuseDiagnostics.Reference(_retailAssetsAccessor()), EnbWorkReuseDiagnostics.Reference(_baseAssetResolverAccessor()));
    }
}
