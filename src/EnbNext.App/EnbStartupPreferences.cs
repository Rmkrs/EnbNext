using EnbNext.Presentation;
namespace EnbNext.App;

// The selected display mode/size is intentionally independent of the native
// window's current placement. In particular, maximizing a window must not turn
// its transient client area into the selected windowed resolution.
internal sealed record EnbStartupPreferences(
    string Server = "sunrise.net-7.org", int? X = null, int? Y = null,
    double Width = 1180, double Height = 760, bool Maximized = false,
    int DisplayWidth = 1280, int DisplayHeight = 720, bool FullScreen = false,
    bool PlayStartupIntro = true, uint? SoundSettingsOwner = null,
    bool IntroductoryTutorialCompleted = false, int BackgroundGameplayFps = 60, double? UiScale = null, bool TextureOverrides = true, string? TexturePack = null, EnbNext.Rendering.EnbEnhancedGraphicsOptions? EnhancedGraphics = null, EnbQualityOfLifeOptions? QualityOfLife = null, EnbDiscordPresenceOptions? DiscordPresence = null,
    bool BackgroundFpsCap = false, double? WaypointArrowX = null, double? WaypointArrowY = null,
    double? MissionTrackerX = null, double? MissionTrackerY = null,
    double? ModernInventoryX = null, double? ModernInventoryY = null,
    double? ModernEquipmentX = null, double? ModernEquipmentY = null,
    double? ModernSkillsX = null, double? ModernSkillsY = null,
    double? ModernMissionsX = null, double? ModernMissionsY = null,
    double? ModernFactionsX = null, double? ModernFactionsY = null, double? ModernCommandsX = null, double? ModernCommandsY = null, double? ModernFriendsX = null, double? ModernFriendsY = null, double? ModernGuildX = null, double? ModernGuildY = null, double? ModernFinderX = null, double? ModernFinderY = null, double? ModernLogX = null, double? ModernLogY = null, double? ModernOptionsX = null, double? ModernOptionsY = null, double? ModernCharactersX = null, double? ModernCharactersY = null, double? ModernMeterX = null, double? ModernMeterY = null, double? ModernTalkX = null, double? ModernTalkY = null, double? ModernTradeX = null, double? ModernTradeY = null, double? ModernLootX = null, double? ModernLootY = null, double? ModernVendorX = null, double? ModernVendorY = null, double? ModernLocalX = null, double? ModernLocalY = null, double? ModernLocalWidth = null, double? ModernLocalRows = null, double? ModernLocalOpacity = null, bool? ModernLocalOpen = null, int? ModernLocalColumns = null, double? ModernLocalStationX = null, double? ModernLocalStationY = null, double? ModernLocalStationWidth = null, double? ModernLocalStationRows = null, double? ModernMusicX = null, double? ModernMusicY = null, double? ModernWikiX = null, double? ModernWikiY = null, double? ModernWikiWidth = null, double? ModernWikiHeight = null, double? ModernItemsX = null, double? ModernItemsY = null, double? ModernItemsWidth = null, double? ModernItemsHeight = null, bool? MusicCombat = null, int? MusicRaces = null, double? ModernStationMapX = null, double? ModernStationMapY = null, double? ModernVaultX = null, double? ModernVaultY = null, double? ModernTerminalX = null, double? ModernTerminalY = null, double? ModernDialogueTextScale = null,
    double? ModernMapX = null, double? ModernMapY = null, double? ModernMapWidth = null, double? ModernMapHeight = null,
    int ModernMapNames = 7, bool ModernMapPointerCoordinates = false, bool ModernMapSeeThrough = true, bool ModernGalaxySchematic = false,
    double? ModernGalaxyX = null, double? ModernGalaxyY = null, double? ModernGalaxyWidth = null, double? ModernGalaxyHeight = null, bool ModernGalaxySeeThrough = true,
    int FocusNameSize = 24, string? GraphicsAdapter = null)
{
    // Advanced Graphics: the cap is off by default; BackgroundGameplayFps keeps the chosen value (0 is the old unchecked state).
    public int BackgroundFpsLimit => BackgroundFpsCap ? (BackgroundGameplayFps > 0 ? BackgroundGameplayFps : 60) : 0;
    public EnbDisplayChoice SelectedDisplay(int desktopWidth, int desktopHeight) => FullScreen
        ? new(desktopWidth, desktopHeight, EnbDisplayMode.BorderlessFullscreen)
        : new(DisplayWidth, DisplayHeight, EnbDisplayMode.Windowed);

    public bool WouldChangeDisplay(EnbDisplayChoice choice, int desktopWidth, int desktopHeight) =>
        choice != SelectedDisplay(desktopWidth, desktopHeight);

    public EnbStartupPreferences WithDisplayChoice(EnbDisplayChoice choice) =>
        choice.Mode == EnbDisplayMode.BorderlessFullscreen
            ? this with { FullScreen = true }
            : this with { DisplayWidth = choice.Width, DisplayHeight = choice.Height,
                FullScreen = false, Maximized = false };

    public EnbStartupPreferences WithNormalPlacement(int x, int y, double width, double height,
        int clientPixelWidth, int clientPixelHeight) => this with
        {
            X = x, Y = y, Width = width, Height = height,
            DisplayWidth = clientPixelWidth, DisplayHeight = clientPixelHeight,
        };
}
