namespace EnbNext.Engine;

/// <summary>Next: the engine's own research captures (asset-captures in the temp folder) are written only when the
/// client was started with --logs; an ordinary game writes none.</summary>
public static class EnbDiagnosticFiles
{
    public static bool Enabled { get; set; }
}
