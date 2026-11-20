namespace EnbNext.App;

// A character's display/placement is local to this machine. Keeping one file
// per avatar also prevents two running clients from rewriting each other's
// profile when they exit in a different order.
internal sealed record EnbCharacterDisplayPreferences(
    bool Enabled = false, int? X = null, int? Y = null,
    double Width = 1180, double Height = 760, bool Maximized = false,
    int DisplayWidth = 1280, int DisplayHeight = 720, bool FullScreen = false, double? UiScale = null)
{
    public static string PathFor(string startupPath, ulong accountId, ulong avatarId) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(startupPath))!, "characters",
            $"{accountId:X16}-{avatarId:X16}.json");

    public EnbStartupPreferences ApplyTo(EnbStartupPreferences startup) => startup with
    {
        X = X, Y = Y, Width = Width, Height = Height, Maximized = Maximized,
        DisplayWidth = DisplayWidth, DisplayHeight = DisplayHeight, FullScreen = FullScreen, UiScale = UiScale,
    };

    public static EnbCharacterDisplayPreferences From(EnbStartupPreferences preferences, bool enabled) => new(
        enabled, preferences.X, preferences.Y, preferences.Width, preferences.Height,
        preferences.Maximized, preferences.DisplayWidth, preferences.DisplayHeight, preferences.FullScreen, preferences.UiScale);
}
