namespace EnbNext.Presentation;

public sealed record EnbQualityOfLifeOptions(bool MuteBackgroundAudio = false,
    bool AutoAcceptGroupInvites = false, bool AutoAcceptTrade = false, bool AutoFormUp = false, bool LootAll = false,
    bool MapSectorShading = false, bool MapMarkers = false, bool WaypointArrow = false,
    int WaypointArrowSize = 100, bool WaypointArrowDistance = true, bool WaypointArrowHeight = false,
    bool MissionTracker = false, bool MissionTrackerStations = false, bool MissionTrackerBackground = true,
    bool MissionTrackerAutoTrack = false, bool ModernUi = false, bool FixedTooltips = false, int ModernChatTextSize = 12,
    bool AlwaysShowLabels = false, int LabelKinds = EnbObjectLabels.All,
    bool BarPercentages = false, bool ExperiencePercentages = false, bool ExperienceValues = false,
    bool RightClickCancelsTarget = false, bool HideCorpses = false, bool KeepLocalOnEscape = false, bool StationFreeMovement = false,
    bool ConnectionIndicator = false, bool AttackerNames = false, bool HideGutterRail = false, int HiddenIconKinds = 0,
    int StationLookSpeed = 100, bool ChatTimes = false, bool ChatKeepHistory = false, bool ChatSaveLogs = false,
    bool RouteWormholes = false, bool LagReports = true, bool LagReportsAutomatic = false)
{
    public const int ArrowSizeMinimum = 50, ArrowSizeMaximum = 200, ArrowSizeStep = 10;
    public int ArrowSizePercent => Math.Clamp(WaypointArrowSize, ArrowSizeMinimum, ArrowSizeMaximum);
    public const int ChatTextMinimum = 10, ChatTextMaximum = 20;
    public int ChatTextSize => Math.Clamp(ModernChatTextSize, ChatTextMinimum, ChatTextMaximum);
    /// <summary>Free station movement: how far the view turns for the mouse's travel, in percent of the standard turn.</summary>
    public const int LookSpeedMinimum = 25, LookSpeedMaximum = 300, LookSpeedStep = 25;
    public int LookSpeedPercent => Math.Clamp(StationLookSpeed, LookSpeedMinimum, LookSpeedMaximum);
}
