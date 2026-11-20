using EnbNext.Engine;

namespace EnbNext.App;

/// <summary>
/// Next: diagnostic files (the session transcript and its presentation, frame and scene receipts, issue and crash
/// reports, wire captures) are written only on request, with --logs on the command line. An ordinary start leaves
/// nothing on disk but settings, saved logins and caches.
/// </summary>
internal static class DiagnosticFiles
{
    public const string Option = "--logs";
    public static bool Enabled { get; private set; }
    /// <summary>The directory diagnostic files go to, or null when they are not written.</summary>
    public static string? Directory => Enabled ? LiveIssueLog.DefaultDirectory : null;

    /// <summary>Takes --logs from the arguments; the rest go on to the login and movie options.</summary>
    public static string[] Take(string[] args, bool force = false)
    {
        Enabled = force || args.Any(a => a.Equals(Option, StringComparison.OrdinalIgnoreCase));
        EnbDiagnosticFiles.Enabled = Enabled;
        return [.. args.Where(a => !a.Equals(Option, StringComparison.OrdinalIgnoreCase))];
    }
}
