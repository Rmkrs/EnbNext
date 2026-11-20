namespace EnbNext.Engine;

// UI-thread owned. No timer, frame observer, or meter exists until the developer opens the window.
internal static class DeveloperRenderDiagnostics
{
    internal static readonly bool Available = Environment.GetEnvironmentVariable("ENBNEXT_DEVELOPER_TOOLS") == "1";
    internal static bool Open { get; private set; }
    internal static bool SessionUsed { get; private set; }
    internal static bool Dazzles = true, Particles = true, Effects = true, Scanner = true, Hud = true,
        Backdrop = true, Shadows = true, Gutter = true, Audio = true;
    internal static int? FocusedCap=null, BackgroundCap=null;
    internal static event Action? Changed;
    internal static string TitlePrefix => SessionUsed ? "EnbNext [DIAGNOSTIC]" : "EnbNext";
    internal static void OpenSession() { Open = true; SessionUsed = true; Changed?.Invoke(); }
    internal static void NotifyChanged() { SessionUsed = true; Changed?.Invoke(); }
    internal static void CloseSession()
    {
        Open = false;
        Dazzles = Particles = Effects = Scanner = Hud = Backdrop = Shadows = Gutter = Audio = true;
        Changed?.Invoke();
    }
    internal static object Receipt() => new { DiagnosticSession = SessionUsed, DeveloperWindowOpen = Open,
        EligibleForParityOrPerformanceAcceptance = !SessionUsed,
        Dazzles, ParticleDrawing = Particles, GeneratedEffectDrawing = Effects, Scanner, Hud, Backdrop, DynamicShadows = Shadows,
        GutterRadar = Gutter, AudioOutput = Audio, FocusedCap, BackgroundCap };
}
