namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    // Retained state is the normal path. An explicit control file preserves
    // the baseline for rollback/comparison without restarting scene owners.
    private readonly string? _retainedSubmissionControl =
        Environment.GetEnvironmentVariable("ENB_EXPERIMENT_RETAINED_SUBMISSION_CONTROL");

    private bool ReadRetainedSubmissionExperiment()
    {
        if (string.IsNullOrWhiteSpace(_retainedSubmissionControl)) return true;
        try { return System.IO.File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(_retainedSubmissionControl)).Trim() == "1"; }
        catch (System.IO.IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
