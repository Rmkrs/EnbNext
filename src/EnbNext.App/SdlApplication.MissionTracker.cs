using System.Collections.Immutable;
using System.Text.Json;
using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Rendering;
using SDL;
namespace EnbNext.App;
internal sealed partial class SdlApplication
{
    // Tracked missions per character (fixed session id), stored next to the startup preferences.
    private Dictionary<uint, List<int>> trackedMissions = [];
    private bool trackedMissionsLoaded;
    private EnbMissionTracker? missionTracker;
    // Slots the server announced as new missions, awaiting their snapshot entry.
    private readonly List<(int Slot, long Since)> pendingNewMissions = [];
    // The last non-empty mission log of this character: a tracked mission missing from the next one was dropped.
    private ImmutableArray<EnbCharacterMissionSnapshot> previousMissions = [];
    private uint? previousMissionsCharacter;

    // Raised on the session thread; the UI thread owns the pending list.
    private void OnNewMissionStarted(int slot) => Post(() => pendingNewMissions.Add((slot, Environment.TickCount64)));
    private string TrackedMissionsPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_preferencesPath))!, "mission-tracker.json");

    private void LoadTrackedMissions()
    {
        if (trackedMissionsLoaded) return; trackedMissionsLoaded = true;
        try
        {
            if (File.Exists(TrackedMissionsPath) && JsonSerializer.Deserialize<Dictionary<uint, List<int>>>(File.ReadAllText(TrackedMissionsPath)) is { } file)
                trackedMissions = file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { AppendDiagnostic("Mission tracker could not be read: " + ex.Message); }
    }

    private void SaveTrackedMissions()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TrackedMissionsPath)!);
            string temporary = TrackedMissionsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, JsonSerializer.Serialize(trackedMissions, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, TrackedMissionsPath, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppendDiagnostic("Mission tracker could not be saved: " + ex.Message); }
    }

    private List<int>? TrackedFor(bool create)
    {
        LoadTrackedMissions();
        if (_client?.SelectedFixedSessionId is not uint character) return null;
        if (!trackedMissions.TryGetValue(character, out var list) && create) trackedMissions[character] = list = [];
        return list;
    }

    private bool IsMissionTracked(int id) => TrackedFor(false)?.Contains(id) == true;

    private void ToggleTrackedMission(int id)
    {
        if (TrackedFor(true) is not { } list) return;
        if (!list.Remove(id)) list.Add(id);
        SaveTrackedMissions();
    }

    /// <summary>Wires the mission log's Track button; the tracker itself follows the options each tick.</summary>
    private void ApplyMissionTrackerOptions(EnbWorldController owner, EnbQualityOfLifeOptions value)
    {
        owner.Inventory.MissionTrackingEnabled = value.MissionTracker;
        owner.Inventory.IsMissionTracked = IsMissionTracked;
        owner.Inventory.MissionTrackToggled = ToggleTrackedMission;
    }

    private void TickMissionTracker()
    {
        var options = EffectiveQualityOfLife;
        var client = _client; var scene = client?.LatestPresentationSnapshot;
        UpdateTrackedMissionSet(options, scene);
        var tracked = TrackedFor(false);
        var entries = ImmutableArray<EnbMissionTrackerEntry>.Empty;
        bool docked = scene?.ActiveStarbaseId is not null;
        if (worldScreen is not null && client?.RetailAssets is not null && options.MissionTracker && tracked is { Count: > 0 } &&
            scene is { IsTransitioning: false } && (!docked || options.MissionTrackerStations) && scene.Hud.Inventory is { } inventory)
        {
            uint time = client.CurrentClientTime ?? scene.ClientTime;
            // Tracking order is the order the player tracked them in.
            entries = [.. tracked.Select(id => inventory.Missions.FirstOrDefault(m => m.Id == id)).OfType<EnbCharacterMissionSnapshot>()
                .Select(m => new EnbMissionTrackerEntry(m.Id, m.Name, m.CurrentStep, m.TimerText(time)))];
        }
        if (entries.IsEmpty)
        {
            if (missionTracker is not null) { if (worldScreen is { } world) world.Owner.Hud.MissionTracker = null; missionTracker.Dispose(); missionTracker = null; }
            return;
        }
        if (missionTracker is null)
        {
            missionTracker = new(client!.RetailAssets!)
            { Position = _preferences.MissionTrackerX is double x && _preferences.MissionTrackerY is double y ? new(x, y) : null };
            missionTracker.Untracked += id => { if (TrackedFor(false) is { } list && list.Remove(id)) SaveTrackedMissions(); };
            missionTracker.Moved += at => { _preferences = _preferences with { MissionTrackerX = at.X, MissionTrackerY = at.Y }; SavePreferences(); };
            // Next Modern UI: a click shows the mission's details (its place is rarely known in the sector, so the arrow
            // would mostly only say so); the classic interface keeps pointing the waypoint arrow.
            missionTracker.Clicked += id => { if (worldScreen?.Owner.Ui.Inventory is { ModernUi: true } inventory) inventory.ShowModernMission(id); else PointArrowAtMission(id); };
        }
        missionTracker.Configure(options.MissionTrackerBackground, options.WaypointArrow, worldScreen?.Owner.Ui.Inventory.ModernUi == true);
        missionTracker.Update(entries);
        worldScreen!.Owner.Hud.MissionTracker = missionTracker;
    }

    /// <summary>
    /// Keeps the tracked set current: completed, failed and expired missions (and ones that vanished
    /// from the log after being seen) drop out; with auto-track on, newly given missions are added.
    /// </summary>
    private void UpdateTrackedMissionSet(EnbQualityOfLifeOptions options, EnbSceneSnapshot? scene)
    {
        long now = Environment.TickCount64;
        if (scene?.Hud.Inventory is not { } inventory || scene.IsTransitioning) return;
        bool autoTrack = options.MissionTracker && options.MissionTrackerAutoTrack;
        var given = new List<EnbCharacterMissionSnapshot>();
        for (int i = pendingNewMissions.Count - 1; i >= 0; i--)
        {
            var (slot, since) = pendingNewMissions[i];
            var mission = inventory.Missions.FirstOrDefault(m => m.Slot == slot);
            if (mission is null && now - since < 10_000) continue;
            pendingNewMissions.RemoveAt(i);
            if (mission is not null) given.Add(mission);
        }
        if (previousMissionsCharacter != _client?.SelectedFixedSessionId) { previousMissions = []; previousMissionsCharacter = _client?.SelectedFixedSessionId; }
        if (TrackedFor(autoTrack && given.Count > 0) is not { } tracked) return;
        bool changed = EnbTrackedMissions.Update(tracked, inventory.Missions, previousMissions, given, autoTrack);
        if (!inventory.Missions.IsEmpty) previousMissions = inventory.Missions;
        if (changed) SaveTrackedMissions();
    }

    /// <summary>Missions carry no coordinates: point the arrow at the object in this sector whose name
    /// the current step mentions (the longest such name), when the waypoint arrow is enabled.</summary>
    private void PointArrowAtMission(int id)
    {
        if (!EffectiveQualityOfLife.WaypointArrow || _client is not { } client || client.LatestPresentationSnapshot is not { } scene ||
            scene.Hud.Inventory?.Missions.FirstOrDefault(m => m.Id == id) is not { } mission || scene.Environment?.SectorNum is not uint sector) return;
        LoadWaypoints();
        var target = EnbMissionTargets.Find(mission.CurrentStep, scene.Objects);
        if (target is { } found)
        {
            string sectorName = client.GalaxyMap?.Sectors.FirstOrDefault(s => s.SectorHashId == sector)?.Name ?? "";
            waypoints.SetActive(new(sector, sectorName, found.Position, found.Name));
        }
        else if (client.SelectedFixedSessionId is uint character)
            client.ReportLocalChatMessage(character, 19, "No target for \"" + mission.Name + "\" found in this sector.");
    }

    private bool HandleMissionTracker(SDL_Event e)
    {
        if (missionTracker is not { } tracker || worldScreen is not { } world || world.Owner.Ui.Layout.Editing) return false;
        var type = (SDL_EventType)e.type;
        if (type == SDL_EventType.SDL_EVENT_MOUSE_MOTION) { tracker.Pointer(new(e.motion.x, e.motion.y)); return tracker.Dragging; }
        if (type == SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP) return tracker.Release();
        if (type != SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN || PointerUnderGameUi(world, e)) return false;
        return e.button.button is 1 or 3 && tracker.Press(new(e.button.x, e.button.y), e.button.button == 3);
    }
}
