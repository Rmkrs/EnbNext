namespace EnbNext.Client;

// Zero disables only the additional background cap, never the foreground cap.
public static class EnbFrameRatePolicy
{
    public static int Resolve(bool gameplay, bool focused, int foreground, int background) =>
        !gameplay ? 60 : !focused && background > 0 ? Math.Min(foreground, background) : foreground;
}
