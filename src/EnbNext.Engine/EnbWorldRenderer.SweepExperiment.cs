using System.Diagnostics;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly string? _sweepExperimentControl = Environment.GetEnvironmentVariable("ENB_EXPERIMENT_INDEXED_SWEEP_CONTROL");
    private long _sweepExperimentPollAfter;
    private bool _sweepExperimentEnabled = true;

    private bool ReadSweepExperiment()
    {
        if (string.IsNullOrWhiteSpace(_sweepExperimentControl)) return true;
        long now = Stopwatch.GetTimestamp();
        if (now < _sweepExperimentPollAfter) return _sweepExperimentEnabled;
        _sweepExperimentPollAfter = now + Stopwatch.Frequency / 2;
        try { _sweepExperimentEnabled = File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(_sweepExperimentControl)).Trim() == "1"; }
        catch (IOException) { _sweepExperimentEnabled = false; }
        catch (UnauthorizedAccessException) { _sweepExperimentEnabled = false; }
        return _sweepExperimentEnabled;
    }
}
